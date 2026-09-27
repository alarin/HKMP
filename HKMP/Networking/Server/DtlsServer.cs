using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Hkmp.Networking.Server;

/// <summary>
/// DTLS implementation for a server-side peer for networking.
/// </summary>
internal class DtlsServer {
    /// <summary>
    /// The maximum packet size for sending and receiving DTLS packets.
    /// </summary>
    public const int MaxPacketSize = 1400;

    /// <summary>
    /// The maximum time in milliseconds that a DTLS handshake with a client can take.
    /// </summary>
    public const int HandshakeTimeoutMillis = 5000;

    /// <summary>
    /// The DTLS server protocol instance from which to start establishing connections with clients.
    /// </summary>
    private DtlsServerProtocol _serverProtocol;

    /// <summary>
    /// The TLS client for communicating supported cipher suites and handling certificates.
    /// </summary>
    private ServerTlsServer _tlsServer;

    /// <summary>
    /// The socket instance for the underlying networking.
    /// The server only uses a single socket for all connections given that with UDP, we cannot create more than one
    /// on the same listening port.
    /// </summary>
    private Socket _socket;

    /// <summary>
    /// The server datagram transport that provides networking to the DTLS server.
    /// </summary>
    private ServerDatagramTransport _currentDatagramTransport;

    /// <summary>
    /// Token source for cancellation tokens for the accept and receive loop tasks.
    /// </summary>
    private CancellationTokenSource _cancellationTokenSource;

    /// <summary>
    /// Dictionary mapping IP endpoints to DTLS server client instances. This keeps track of individual clients
    /// connected to the server and their respective objects.
    /// </summary>
    private readonly ConcurrentDictionary<IPEndPoint, DtlsServerClient> _dtlsClients;

    /// <summary>
    /// Event that is called when data is received from the given DTLS server client.
    /// </summary>
    public event Action<DtlsServerClient, byte[], int> DataReceivedEvent;

    /// <summary>
    /// The port that the server is started on.
    /// </summary>
    private int _port;

    public DtlsServer() {
        _dtlsClients = new ConcurrentDictionary<IPEndPoint, DtlsServerClient>();
    }

    /// <summary>
    /// Start the DTLS server on the given port.
    /// </summary>
    /// <param name="port">The port to start listening on.</param>
    public void Start(int port) {
        _port = port;

        _serverProtocol = new DtlsServerProtocol();
        _tlsServer = new ServerTlsServer(new BcTlsCrypto());

        _cancellationTokenSource = new CancellationTokenSource();

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, _port));

        new Thread(() => AcceptLoop(_cancellationTokenSource.Token)).Start();
        new Thread(() => SocketReceiveLoop(_cancellationTokenSource.Token)).Start();
    }

    /// <summary>
    /// Stop the DTLS server by disconnecting all clients and cancelling all running threads.
    /// </summary>
    public void Stop() {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _currentDatagramTransport?.Close();

        _tlsServer?.Cancel();

        _socket?.Close();
        _socket = null;

        foreach (var dtlsServerClient in _dtlsClients.Values) {
            InternalDisconnectClient(dtlsServerClient);
        }

        _dtlsClients.Clear();
    }

    /// <summary>
    /// Disconnect the client with the given IP endpoint from the server.
    /// </summary>
    /// <param name="endPoint">The IP endpoint of the client.</param>
    public void DisconnectClient(IPEndPoint endPoint) {
        if (!_dtlsClients.TryRemove(endPoint, out var dtlsServerClient)) {
            Logger.Warn("Could not find DtlsServerClient to disconnect");
            return;
        }

        InternalDisconnectClient(dtlsServerClient);
    }

    /// <summary>
    /// Disconnect the given DTLS server client from the server. This will request cancellation of the "receive loop"
    /// for the client and close/cleanup the underlying DTLS objects.
    /// </summary>
    /// <param name="dtlsServerClient"></param>
    private void InternalDisconnectClient(DtlsServerClient dtlsServerClient) {
        dtlsServerClient.ReceiveLoopTokenSource?.Cancel();
        dtlsServerClient.ReceiveLoopTokenSource?.Dispose();

        dtlsServerClient.DtlsTransport?.Close();
        dtlsServerClient.DatagramTransport.Dispose();
    }

    /// <summary>
    /// Start a loop that will continuously receive data on the socket for existing and new clients.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the loop.</param>
    private void SocketReceiveLoop(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);

            var numReceived = 0;
            var buffer = new byte[MaxPacketSize];

            try {
                numReceived = _socket.ReceiveFrom(
                    buffer,
                    SocketFlags.None,
                    ref endPoint
                );
            } catch (SocketException e) when (e.SocketErrorCode == SocketError.Interrupted) {
                // SocketError Interrupted happens when the socket is closed during the receive call
                // We close the socket when the server is stopped, thus this exception is expected, so we simply break
                break;
            } catch (SocketException e) {
                Logger.Error(
                    $"UDP Socket exception, ErrorCode: {e.ErrorCode}, Socket ErrorCode: {e.SocketErrorCode}, Exception:\n{e}");
            }

            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            var ipEndPoint = (IPEndPoint) endPoint;

            ServerDatagramTransport serverDatagramTransport;
            if (!_dtlsClients.TryGetValue(ipEndPoint, out var dtlsServerClient)) {
                Logger.Debug($"Received data on server socket from unknown IP ({ipEndPoint}), length: {numReceived}");

                serverDatagramTransport = _currentDatagramTransport;
                if (serverDatagramTransport == null) {
                    continue;
                }

                // Only one handshake can run on the current transport. The first ClientHello claims it for its
                // endpoint and datagrams from other unknown endpoints are dropped until that handshake is done,
                // their DTLS clients retransmit. Mixing datagrams of two endpoints breaks both handshakes.
                var transportEndPoint = serverDatagramTransport.IPEndPoint;
                if (transportEndPoint == null) {
                    if (!IsClientHello(buffer, numReceived)) {
                        continue;
                    }

                    // Set the IP endpoint of the datagram transport instance so it can send data to the correct IP
                    serverDatagramTransport.IPEndPoint = ipEndPoint;
                    serverDatagramTransport.HandshakeStarted.Set();
                } else if (!transportEndPoint.Equals(ipEndPoint)) {
                    continue;
                }
            } else {
                serverDatagramTransport = dtlsServerClient.DatagramTransport;
            }

            try {
                serverDatagramTransport.ReceivedDataCollection.Add(new UdpDatagramTransport.ReceivedData {
                    Buffer = buffer,
                    Length = numReceived
                }, cancellationToken);
            } catch (OperationCanceledException) {
                break;
            } catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) {
                // The transport was disposed in the meantime, because the client disconnected or its handshake
                // failed, so the datagram has no receiver anymore
            }
        }
    }

    /// <summary>
    /// Whether the given datagram is a DTLS record with the first ClientHello of a handshake.
    /// </summary>
    /// <param name="buffer">The buffer containing the datagram.</param>
    /// <param name="length">The length of the datagram.</param>
    /// <returns>True if the datagram starts a handshake, false otherwise.</returns>
    private static bool IsClientHello(byte[] buffer, int length) {
        // The 13-byte DTLS record header starts with content type 22 (handshake) and has epoch 0 at offset 3,
        // it is followed by the handshake message type, which is 1 for client_hello
        return length > 13 && buffer[0] == 22 && buffer[3] == 0 && buffer[4] == 0 && buffer[13] == 1;
    }

    /// <summary>
    /// Start a loop that will continuously accept new clients on the DTLS protocol for new incoming connections.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to cancel the loop.</param>
    private void AcceptLoop(CancellationToken cancellationToken) {
        var serverProtocol = _serverProtocol;
        ServerDatagramTransport datagramTransport = null;

        while (!cancellationToken.IsCancellationRequested) {
            Logger.Debug("Creating new ServerDatagramTransport for handling new connection");

            datagramTransport = new ServerDatagramTransport(_socket);
            _currentDatagramTransport = datagramTransport;

            // The handshake timeout counts from the start of Accept, so only start accepting once a ClientHello
            // arrived. Otherwise a client that starts its handshake just before the timeout gets cut off halfway.
            WaitHandle.WaitAny([datagramTransport.HandshakeStarted.WaitHandle, cancellationToken.WaitHandle]);
            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            DtlsTransport dtlsTransport;
            try {
                dtlsTransport = serverProtocol.Accept(_tlsServer, datagramTransport);
            } catch (TlsFatalAlert e) when (e.AlertDescription == AlertDescription.user_canceled) {
                break;
            } catch (IOException e) {
                if (cancellationToken.IsCancellationRequested) {
                    break;
                }

                // A failed handshake only concerns that one client, so we keep accepting new connections
                if (e is TlsTimeoutException) {
                    Logger.Debug("Timeout while accepting DTLS connection");
                } else {
                    Logger.Error($"IOException while accepting DTLS connection:\n{e}");
                }

                datagramTransport.Dispose();
                continue;
            }

            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            var endPoint = datagramTransport.IPEndPoint;

            Logger.Debug($"Accepted DTLS connection on socket, endpoint: {endPoint}");

            if (_dtlsClients.ContainsKey(endPoint)) {
                Logger.Error($"DtlsClient with endpoint ({endPoint}) already exists, cannot add");
                continue;
            }

            var dtlsServerClient = new DtlsServerClient {
                DtlsTransport = dtlsTransport,
                DatagramTransport = datagramTransport,
                EndPoint = endPoint,
                ReceiveLoopTokenSource = new CancellationTokenSource()
            };

            _dtlsClients[endPoint] = dtlsServerClient;

            Logger.Debug("Starting receive loop for client");
            new Thread(() => ClientReceiveLoop(
                dtlsServerClient,
                dtlsServerClient.ReceiveLoopTokenSource.Token
            )).Start();
        }

        datagramTransport?.Dispose();
    }

    /// <summary>
    /// Start a loop for the given DTLS server client that will continuously check whether new data is available
    /// on the DTLS transport for that client. Will evoke the <seealso cref="DataReceivedEvent"/> in case data is
    /// received for that client.
    /// </summary>
    /// <param name="dtlsServerClient">The DTLS server client to receive data for.</param>
    /// <param name="cancellationToken">The cancellation token to cancel to loop.</param>
    private void ClientReceiveLoop(DtlsServerClient dtlsServerClient, CancellationToken cancellationToken) {
        var dtlsTransport = dtlsServerClient.DtlsTransport;

        while (!cancellationToken.IsCancellationRequested) {
            var buffer = new byte[dtlsTransport.GetReceiveLimit()];

            int numReceived;

            try {
                numReceived = dtlsTransport.Receive(buffer, 0, dtlsTransport.GetReceiveLimit(), 5);
            } catch (TlsFatalAlert alert) {
                Logger.Debug($"DtlsServerClient receive call TLS fatal alert: {alert.Message}");
                continue;
            }

            if (numReceived <= 0) {
                continue;
            }

            try {
                DataReceivedEvent?.Invoke(dtlsServerClient, buffer, numReceived);
            } catch (Exception e) {
                Logger.Error($"Error occurred while invoking DataReceivedEvent:\n{e}");
            }
        }
    }
}
