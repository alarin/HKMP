using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Hkmp.NetStress;

/// <summary>
/// Fault settings applied to each forwarded datagram, independently per direction.
/// </summary>
internal class FaultSettings {
    public double Loss;
    public double Duplicate;
    public double Reorder;
    public int ReorderDelayMs = 20;
    public int DelayMs;
    public int JitterMs;
}

/// <summary>
/// UDP proxy between one client and the server. The client talks to the listen port, the proxy talks to the
/// server from its own socket bound to a distinct loopback address, so the server sees one endpoint per client.
/// Faults are only injected while <see cref="FaultsEnabled"/> is set.
/// </summary>
internal class UdpProxy : IDisposable {
    private readonly Socket _clientSide;
    private readonly Socket _serverSide;
    private readonly IPEndPoint _serverEndPoint;
    private readonly FaultSettings _faults;
    private readonly Random _random;
    private readonly object _randomLock = new object();

    private readonly SortedList<(long, long), (Socket, byte[], EndPoint)> _scheduled = new();
    private readonly object _scheduleLock = new object();
    private readonly AutoResetEvent _scheduleSignal = new AutoResetEvent(false);
    private long _scheduleCounter;

    private EndPoint _clientEndPoint;
    private volatile bool _running = true;

    public volatile bool FaultsEnabled;

    public long Forwarded, Dropped, Duplicated, Reordered, FullMtu;

    public UdpProxy(int listenPort, IPAddress upstreamAddress, IPEndPoint serverEndPoint, FaultSettings faults,
        int seed) {
        _serverEndPoint = serverEndPoint;
        _faults = faults;
        _random = new Random(seed);

        _clientSide = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _clientSide.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));
        _serverSide = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _serverSide.Bind(new IPEndPoint(upstreamAddress, 0));
        IgnoreConnectionReset(_clientSide);
        IgnoreConnectionReset(_serverSide);

        new Thread(ClientToServer) { IsBackground = true, Name = "proxy c->s" }.Start();
        new Thread(ServerToClient) { IsBackground = true, Name = "proxy s->c" }.Start();
        new Thread(SendScheduled) { IsBackground = true, Name = "proxy sched" }.Start();
    }

    private static void IgnoreConnectionReset(Socket socket) {
        // Stop ICMP port unreachable from surfacing as exceptions on Windows (SIO_UDP_CONNRESET)
        const int sioUdpConnReset = -1744830452;
        try {
            socket.IOControl(sioUdpConnReset, new byte[] { 0 }, null);
        } catch (Exception) {
            // Not supported on this platform
        }
    }

    private void ClientToServer() {
        var buffer = new byte[65536];
        while (_running) {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try {
                length = _clientSide.ReceiveFrom(buffer, ref from);
            } catch (Exception) {
                if (!_running) return;
                continue;
            }

            _clientEndPoint = from;
            Forward(_serverSide, buffer, length, _serverEndPoint);
        }
    }

    private void ServerToClient() {
        var buffer = new byte[65536];
        while (_running) {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try {
                length = _serverSide.ReceiveFrom(buffer, ref from);
            } catch (Exception) {
                if (!_running) return;
                continue;
            }

            var client = _clientEndPoint;
            if (client != null) {
                Forward(_clientSide, buffer, length, client);
            }
        }
    }

    private double NextDouble() {
        lock (_randomLock) {
            return _random.NextDouble();
        }
    }

    private int NextInt(int max) {
        lock (_randomLock) {
            return _random.Next(max);
        }
    }

    private void Forward(Socket socket, byte[] buffer, int length, EndPoint to) {
        var data = new byte[length];
        Buffer.BlockCopy(buffer, 0, data, 0, length);
        // A full MTU part of a split update packet is 1200 bytes plus DTLS record overhead
        if (length >= 1200) Interlocked.Increment(ref FullMtu);

        if (!FaultsEnabled) {
            Send(socket, data, to);
            return;
        }

        if (NextDouble() < _faults.Loss) {
            Interlocked.Increment(ref Dropped);
            return;
        }

        var copies = NextDouble() < _faults.Duplicate ? 2 : 1;
        if (copies == 2) Interlocked.Increment(ref Duplicated);

        for (var i = 0; i < copies; i++) {
            var delay = _faults.DelayMs;
            if (_faults.JitterMs > 0) delay += NextInt(_faults.JitterMs + 1);
            if (NextDouble() < _faults.Reorder) {
                // Hold this datagram back so later ones overtake it
                delay += _faults.ReorderDelayMs;
                Interlocked.Increment(ref Reordered);
            }

            if (delay <= 0) {
                Send(socket, data, to);
            } else {
                Schedule(socket, data, to, delay);
            }
        }
    }

    private void Send(Socket socket, byte[] data, EndPoint to) {
        try {
            socket.SendTo(data, to);
            Interlocked.Increment(ref Forwarded);
        } catch (Exception) {
            // Socket closed or endpoint gone, same as a lost datagram
        }
    }

    private void Schedule(Socket socket, byte[] data, EndPoint to, int delayMs) {
        var due = Environment.TickCount + delayMs;
        lock (_scheduleLock) {
            _scheduled.Add((due, _scheduleCounter++), (socket, data, to));
        }

        _scheduleSignal.Set();
    }

    private void SendScheduled() {
        while (_running) {
            (Socket, byte[], EndPoint)? next = null;
            var wait = 50;
            lock (_scheduleLock) {
                if (_scheduled.Count > 0) {
                    var key = _scheduled.Keys[0];
                    var remaining = (int) (key.Item1 - Environment.TickCount);
                    if (remaining <= 0) {
                        next = _scheduled.Values[0];
                        _scheduled.RemoveAt(0);
                        wait = 0;
                    } else {
                        wait = remaining;
                    }
                }
            }

            if (next.HasValue) {
                Send(next.Value.Item1, next.Value.Item2, next.Value.Item3);
            } else {
                _scheduleSignal.WaitOne(wait);
            }
        }
    }

    public void Dispose() {
        _running = false;
        _clientSide.Close();
        _serverSide.Close();
        _scheduleSignal.Set();
    }
}
