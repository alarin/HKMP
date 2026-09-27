using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Hkmp.Game.Settings;
using Hkmp.Logging;
using Hkmp.Math;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using Hkmp.Util;

namespace Hkmp.NetStress;

/// <summary>
/// Headless stress test of the HKMP networking layer: a real NetServer and real NetClients connected through
/// fault-injecting UDP proxies, exchanging reliable save updates (checked for loss, duplication and corruption)
/// and unreliable entity updates, with periodic bursts that make update packets exceed the MTU.
/// </summary>
internal static class Program {
    private static readonly Options Opt = new Options();
    private static readonly CountingLogger Log = new CountingLogger();

    private static readonly ConcurrentDictionary<string, StreamStats> Streams = new();

    private static int Main(string[] args) {
        Opt.Parse(args);
        Directory.CreateDirectory(Opt.LogDir);
        var logPath = Path.Combine(Opt.LogDir, $"{Opt.Name}.log");
        Log.Open(logPath);
        Logger.AddLogger(Log);

        var pump = new Thread(PumpMainThreadActions) { IsBackground = true, Name = "main-thread pump" };
        pump.Start();

        var serverPacketManager = new PacketManager();
        var server = new NetServer(serverPacketManager);
        var clientIds = new ConcurrentDictionary<string, ushort>();
        var serverTimeouts = 0;

        server.ConnectionRequestEvent += (client, clientInfo, serverInfo) => {
            serverInfo.ConnectionResult = ServerConnectionResult.Accepted;
            serverInfo.AddonOrder = [];
            serverInfo.ServerSettingsUpdate = new ServerSettingsUpdate { ServerSettings = new ServerSettings() };
            serverInfo.CurrentSave = new CurrentSave();
            serverInfo.PlayerInfo = [];
            clientIds[clientInfo.Username] = client.Id;
        };
        server.ClientTimeoutEvent += _ => Interlocked.Increment(ref serverTimeouts);

        // The packet manager unpacks data collections and calls the handler per instance
        serverPacketManager.RegisterServerUpdatePacketHandler<SaveUpdate>(
            ServerUpdatePacketId.SaveUpdate,
            (id, update) => {
                var username = clientIds.FirstOrDefault(p => p.Value == id).Key ?? $"id{id}";
                Receive($"{username}->server", update.Value);
            }
        );
        serverPacketManager.RegisterServerUpdatePacketHandler<IPacketData>(ServerUpdatePacketId.EntityUpdate,
            (_, _) => { });

        server.Start(Opt.Port);

        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, Opt.Port);
        var proxies = new List<UdpProxy>();
        var clients = new List<(string Name, NetClient Client)>();
        var clientTimeouts = 0;

        for (var i = 0; i < Opt.Clients; i++) {
            var name = $"bot{i}";
            var proxyPort = Opt.Port + 1 + i;
            // Distinct loopback source address per client, like different machines on a LAN
            var upstream = IPAddress.Parse($"127.0.0.{2 + i}");
            var proxy = new UdpProxy(proxyPort, upstream, serverEndPoint, Opt.Faults, Opt.Seed + i) {
                FaultsEnabled = Opt.FaultsDuringConnect
            };
            proxies.Add(proxy);

            var packetManager = new PacketManager();
            packetManager.RegisterClientUpdatePacketHandler<SaveUpdate>(
                ClientUpdatePacketId.SaveUpdate,
                update => Receive($"server->{name}", update.Value)
            );
            packetManager.RegisterClientUpdatePacketHandler<IPacketData>(ClientUpdatePacketId.EntityUpdate, _ => { });

            var client = new NetClient(packetManager);
            client.UpdateManager.TimeoutEvent += () => Interlocked.Increment(ref clientTimeouts);
            client.Connect("127.0.0.1", proxyPort, name, $"stress-auth-key-{name}", []);
            clients.Add((name, client));

            if (!Opt.SimultaneousConnect) {
                // Players normally join one after another; wait for this one before starting the next
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < Opt.ConnectTimeoutMs &&
                       client.ConnectionStatus != ClientConnectionStatus.Connected) {
                    Thread.Sleep(20);
                }
            }
        }

        var connectWatch = Stopwatch.StartNew();
        while (connectWatch.ElapsedMilliseconds < Opt.ConnectTimeoutMs &&
               clients.Any(c => c.Client.ConnectionStatus != ClientConnectionStatus.Connected)) {
            Thread.Sleep(50);
        }

        var connected = clients.Count(c => c.Client.ConnectionStatus == ClientConnectionStatus.Connected);
        Log.Line($"connected {connected}/{clients.Count} after {connectWatch.ElapsedMilliseconds} ms");
        var connectOk = connected == clients.Count && clientIds.Count == clients.Count;

        var trafficWatch = Stopwatch.StartNew();
        if (connectOk) {
            foreach (var proxy in proxies) {
                proxy.FaultsEnabled = true;
            }

            RunTraffic(server, clients, clientIds);
        }

        var trafficMs = trafficWatch.ElapsedMilliseconds;

        // Stop injecting faults and let the reliability layer catch up
        foreach (var proxy in proxies) {
            proxy.FaultsEnabled = false;
        }

        if (connectOk) {
            Thread.Sleep(Opt.DrainMs);
        }

        var result = Report(connectOk, connected, trafficMs, serverTimeouts, clientTimeouts, proxies);

        foreach (var (_, client) in clients) {
            try {
                client.Disconnect();
            } catch (Exception e) {
                Log.Line($"client disconnect threw: {e.Message}");
            }
        }

        try {
            server.Stop();
        } catch (Exception e) {
            Log.Line($"server stop threw: {e.Message}");
        }

        foreach (var proxy in proxies) {
            proxy.Dispose();
        }

        Log.Close();
        // Background threads of the networking layer would keep the process alive
        Environment.Exit(result ? 0 : 1);
        return 0;
    }

    private static void RunTraffic(
        NetServer server,
        List<(string Name, NetClient Client)> clients,
        ConcurrentDictionary<string, ushort> clientIds
    ) {
        var seqs = new Dictionary<string, int>();
        var tick = 0;
        var watch = Stopwatch.StartNew();
        var random = new Random(Opt.Seed);

        while (watch.ElapsedMilliseconds < Opt.DurationMs) {
            var items = tick % Opt.BurstEveryTicks == 0 ? Opt.BurstItems : Opt.ItemsPerTick;

            foreach (var (name, client) in clients) {
                if (client.ConnectionStatus != ClientConnectionStatus.Connected) continue;

                var up = $"{name}->server";
                for (var i = 0; i < items; i++) {
                    var seq = Next(seqs, up);
                    client.UpdateManager.SetSaveUpdate((ushort) seq, Payload(up, seq));
                }

                for (ushort e = 0; e < Opt.Entities; e++) {
                    client.UpdateManager.UpdateEntityPosition(e,
                        new Vector2((float) random.NextDouble(), (float) random.NextDouble()));
                }

                var id = clientIds[name];
                var updateManager = server.GetUpdateManagerForClient(id);
                if (updateManager == null) continue;

                var down = $"server->{name}";
                for (var i = 0; i < items; i++) {
                    var seq = Next(seqs, down);
                    updateManager.SetSaveUpdate((ushort) seq, Payload(down, seq));
                }

                for (ushort e = 0; e < Opt.Entities; e++) {
                    updateManager.UpdateEntityPosition(e,
                        new Vector2((float) random.NextDouble(), (float) random.NextDouble()));
                }
            }

            tick++;
            Thread.Sleep(Opt.TickMs);
        }
    }

    private static int Next(Dictionary<string, int> seqs, string stream) {
        seqs.TryGetValue(stream, out var seq);
        seqs[stream] = seq + 1;
        Streams.GetOrAdd(stream, s => new StreamStats(s)).Sent = seq + 1;
        return seq;
    }

    private static int Tag(string stream) {
        unchecked {
            var hash = 17;
            foreach (var c in stream) hash = hash * 31 + c;
            return hash;
        }
    }

    private static byte[] Payload(string stream, int seq) {
        var data = new byte[Opt.ItemSize];
        var tag = Tag(stream);
        BitConverter.GetBytes(seq).CopyTo(data, 0);
        BitConverter.GetBytes(tag).CopyTo(data, 4);
        for (var i = 8; i < data.Length - 4; i++) {
            data[i] = (byte) (seq * 31 + i * 7 + tag);
        }

        BitConverter.GetBytes(Checksum(data)).CopyTo(data, data.Length - 4);
        return data;
    }

    private static int Checksum(byte[] data) {
        unchecked {
            var hash = (int) 2166136261;
            for (var i = 0; i < data.Length - 4; i++) hash = (hash ^ data[i]) * 16777619;
            return hash;
        }
    }

    private static void Receive(string stream, byte[] data) {
        var stats = Streams.GetOrAdd(stream, s => new StreamStats(s));
        if (data.Length < 12 || BitConverter.ToInt32(data, data.Length - 4) != Checksum(data) ||
            BitConverter.ToInt32(data, 4) != Tag(stream)) {
            Interlocked.Increment(ref stats.Corrupt);
            return;
        }

        var seq = BitConverter.ToInt32(data, 0);
        lock (stats) {
            if (!stats.Received.Add(seq)) {
                stats.Duplicates++;
            } else if (seq < stats.MaxSeq) {
                stats.OutOfOrder++;
            }

            stats.MaxSeq = System.Math.Max(stats.MaxSeq, seq);
        }
    }

    private static bool Report(bool connectOk, int connected, long trafficMs, int serverTimeouts,
        int clientTimeouts, List<UdpProxy> proxies) {
        var ok = connectOk && serverTimeouts == 0 && clientTimeouts == 0;
        var lines = new List<string>();
        long missingTotal = 0, dupTotal = 0, corruptTotal = 0;

        foreach (var stats in Streams.Values.OrderBy(s => s.Name)) {
            int received, missing;
            lock (stats) {
                received = stats.Received.Count;
                missing = stats.Sent - stats.Received.Count(s => s < stats.Sent);
            }

            missingTotal += missing;
            dupTotal += stats.Duplicates;
            corruptTotal += stats.Corrupt;
            lines.Add(
                $"  {stats.Name,-16} sent {stats.Sent,6} recv {received,6} missing {missing,6} dup {stats.Duplicates,5} corrupt {stats.Corrupt,4} ooo {stats.OutOfOrder,5}");
        }

        ok &= missingTotal == 0 && dupTotal == 0 && corruptTotal == 0;
        var parse = Log.Counts;
        ok &= parse.ServerParse == 0 && parse.ClientParse == 0 && parse.HandlerErrors == 0;

        var summary = string.Format(CultureInfo.InvariantCulture,
            "RESULT {0} | {1} clients, connected {2}, traffic {3} ms | timeouts server {4} client {5} | " +
            "parse errors server {6} client {7} | handler errors {8} | missing {9} dup {10} corrupt {11} | " +
            "proxy dropped {12} duplicated {13} reordered {14} full-MTU datagrams {15}",
            ok ? "PASS" : "FAIL", Opt.Clients, connected, trafficMs, serverTimeouts, clientTimeouts,
            parse.ServerParse, parse.ClientParse, parse.HandlerErrors, missingTotal, dupTotal, corruptTotal,
            proxies.Sum(p => p.Dropped), proxies.Sum(p => p.Duplicated), proxies.Sum(p => p.Reordered),
            proxies.Sum(p => p.FullMtu));

        Log.Line($"scenario {Opt.Name}: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        foreach (var line in lines) Log.Line(line);
        Log.Line(summary);

        Console.WriteLine($"[{Opt.Name}] {summary}");
        foreach (var line in lines) Console.WriteLine(line);
        return ok;
    }

    /// <summary>
    /// Packet handlers of NetClient run through ThreadUtil on the Unity main thread; without Unity we drain its
    /// queue ourselves.
    /// </summary>
    private static void PumpMainThreadActions() {
        var type = typeof(ThreadUtil);
        var lockObject = type.GetField("Lock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var actions = (List<Action>) type.GetField("ActionsToRun", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

        while (true) {
            List<Action> toRun;
            lock (lockObject) {
                toRun = new List<Action>(actions);
                actions.Clear();
            }

            foreach (var action in toRun) {
                try {
                    action();
                } catch (Exception e) {
                    Log.Line($"main-thread action threw: {e}");
                }
            }

            Thread.Sleep(5);
        }
    }
}

internal class StreamStats {
    public readonly string Name;
    public int Sent;
    public readonly HashSet<int> Received = new();
    public int MaxSeq = -1;
    public long Duplicates, OutOfOrder, Corrupt;

    public StreamStats(string name) {
        Name = name;
    }
}

internal class Options {
    public string Name = "run";
    public int Port = 27950;
    public int Clients = 2;
    public int DurationMs = 20000;
    public int DrainMs = 8000;
    public int ConnectTimeoutMs = 15000;
    public int TickMs = 17;
    public int ItemsPerTick = 1;
    public int BurstEveryTicks = 30;
    public int BurstItems = 10;
    public int ItemSize = 160;
    public int Entities = 30;
    public int Seed = 1;
    public bool FaultsDuringConnect;
    public bool SimultaneousConnect;
    public string LogDir = "logs";
    public readonly FaultSettings Faults = new FaultSettings();

    public void Parse(string[] args) {
        for (var i = 0; i < args.Length; i++) {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i]) {
                case "--name": Name = value; i++; break;
                case "--port": Port = int.Parse(value); i++; break;
                case "--clients": Clients = int.Parse(value); i++; break;
                case "--duration": DurationMs = (int) (double.Parse(value, CultureInfo.InvariantCulture) * 1000); i++; break;
                case "--drain": DrainMs = (int) (double.Parse(value, CultureInfo.InvariantCulture) * 1000); i++; break;
                case "--items": ItemsPerTick = int.Parse(value); i++; break;
                case "--burst-every": BurstEveryTicks = int.Parse(value); i++; break;
                case "--burst-items": BurstItems = int.Parse(value); i++; break;
                case "--item-size": ItemSize = int.Parse(value); i++; break;
                case "--entities": Entities = int.Parse(value); i++; break;
                case "--seed": Seed = int.Parse(value); i++; break;
                case "--loss": Faults.Loss = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--dup": Faults.Duplicate = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--reorder": Faults.Reorder = double.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--reorder-delay": Faults.ReorderDelayMs = int.Parse(value); i++; break;
                case "--delay": Faults.DelayMs = int.Parse(value); i++; break;
                case "--jitter": Faults.JitterMs = int.Parse(value); i++; break;
                case "--faults-during-connect": FaultsDuringConnect = true; break;
                case "--simultaneous-connect": SimultaneousConnect = true; break;
                case "--log-dir": LogDir = value; i++; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
    }
}

/// <summary>
/// Logger that writes everything to a file and counts parse failures per side, telling server from client by
/// the call stack.
/// </summary>
internal class CountingLogger : ILogger {
    public class ParseCounts {
        public long ServerParse, ClientParse, HandlerErrors;
    }

    public readonly ParseCounts Counts = new ParseCounts();
    private StreamWriter _writer;
    private readonly object _lock = new object();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Open(string path) {
        _writer = new StreamWriter(path, false) { AutoFlush = true };
    }

    public void Close() {
        lock (_lock) {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Line(string message) => Write("HARNESS", message);

    private void Write(string level, string message) {
        lock (_lock) {
            _writer?.WriteLine($"{_clock.ElapsedMilliseconds,7} [{level}] {message}");
        }
    }

    private void Classify(string message) {
        if (message.StartsWith("Exception while reading") || message.StartsWith("Received malformed connection")) {
            var stack = new StackTrace().ToString();
            if (stack.Contains("Hkmp.Networking.Server.NetServer")) {
                Interlocked.Increment(ref Counts.ServerParse);
            } else {
                Interlocked.Increment(ref Counts.ClientParse);
            }
        } else if (message.StartsWith("Exception occured while executing") ||
                   message.StartsWith("There is no")) {
            Interlocked.Increment(ref Counts.HandlerErrors);
        }
    }

    public void Info(string message) {
        Classify(message);
        Write("INFO", message);
    }

    public void Fine(string message) {
    }

    public void Debug(string message) {
        Classify(message);
        Write("DEBUG", message);
    }

    public void Warn(string message) {
        Classify(message);
        Write("WARN", message);
    }

    public void Error(string message) {
        Classify(message);
        Write("ERROR", message);
    }
}
