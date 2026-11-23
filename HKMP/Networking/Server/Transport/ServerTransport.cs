using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;
using Hkmp.Networking.Timing;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// The DTLS transport of the server: it binds the socket and composes the network thread, the cookie check, the
/// handshake pool and the peers. The server sees only established clients and their decrypted data.
/// </summary>
internal sealed class ServerTransport : IPeerListener
{
    /// <summary>
    /// The Windows socket control code that stops an ICMP "port unreachable" from failing the next receive call.
    /// </summary>
    private const int SioUdpConnReset = -1744830452;

    private readonly ConnectionTimeouts _timeouts;

    private readonly ConcurrentDictionary<IPEndPoint, DtlsServerClient> _clients = new();
    private readonly HandshakePool _pool = new();
    private Socket _socket;
    private CancellationTokenSource _stopping;
    private NetworkCommands _commands;
    private PeerDemux _demux;
    private Supervisor _supervisor;

    /// <summary>
    /// Creates the transport with the deadlines of its handshakes and of its stop.
    /// </summary>
    /// <param name="timeouts">The deadlines.</param>
    public ServerTransport(ConnectionTimeouts timeouts)
    {
        _timeouts = timeouts;
    }

    /// <summary>
    /// Called on the network thread when decrypted data arrives from a client.
    /// </summary>
    public event Action<DtlsServerClient, byte[], int> DataReceivedEvent;

    /// <summary>
    /// Called on the network thread when the supervisor gave up on the network loop.
    /// </summary>
    public event Action LoopFailed;

    /// <summary>
    /// The clients with an established session.
    /// </summary>
    public IReadOnlyCollection<DtlsServerClient> Clients => (IReadOnlyCollection<DtlsServerClient>) _clients.Values;

    /// <summary>
    /// The number of handshakes running now.
    /// </summary>
    public int HandshakesRunning => _pool.Running;

    /// <summary>
    /// Binds the port and starts the network thread. A port that cannot be bound throws and leaves the transport
    /// stopped, so that the next attempt starts from scratch.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    public void Start(int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            socket.Close();
            throw;
        }

        IgnoreConnectionResets(socket);

        _socket = socket;
        _stopping = new CancellationTokenSource();
        _commands = new NetworkCommands();
        _demux = new PeerDemux(
            socket,
            new CookieGate(new BcTlsCrypto(), socket),
            _pool,
            _commands,
            this,
            () => new ServerTlsServer(new BcTlsCrypto(), _timeouts.ServerHandshake));
        NetworkLoop loop = new(socket, _demux, _commands);

        _supervisor = new Supervisor();
        _supervisor.Faulted += (name, e) =>
        {
            Logger.Error($"{name} stopped after repeated failures: {e.Message}");
            LoopFailed?.Invoke();
        };
        _supervisor.Start("Server network loop", loop.Run, _stopping.Token);
    }

    /// <summary>
    /// Stops the network thread and ends every peer.
    /// </summary>
    /// <returns>False if the network thread was still running when the stop deadline passed.</returns>
    public bool Stop()
    {
        if (_stopping == null)
        {
            return true;
        }

        _stopping.Cancel();
        _socket.Close();
        bool ended = _supervisor.Join(_timeouts.ServerStop);
        if (!ended)
        {
            Logger.Warn("The server network loop did not stop in time");
        }

        // The network thread has ended, so this thread may now touch the peers.
        _demux.CloseAll();
        _clients.Clear();

        _stopping.Dispose();
        _stopping = null;
        _socket = null;
        _demux = null;
        return ended;
    }

    /// <summary>
    /// Ends the session of the client at the endpoint.
    /// </summary>
    /// <param name="endPoint">The endpoint of the client.</param>
    public void DisconnectClient(IPEndPoint endPoint)
    {
        // The peer closes on the network thread a moment later. The client leaves the list now, so that data it
        // sends meanwhile is dropped instead of reaching the server as an unknown client.
        _clients.TryRemove(endPoint, out _);
        PeerDemux demux = _demux;
        _commands?.Post(() => demux?.Close(endPoint));
    }

    /// <summary>
    /// Ends the given client's session only, so a newer session on the same endpoint is left alone.
    /// </summary>
    /// <param name="client">The client.</param>
    public void DisconnectClient(DtlsServerClient client)
    {
        ((ICollection<KeyValuePair<IPEndPoint, DtlsServerClient>>) _clients).Remove(
            new KeyValuePair<IPEndPoint, DtlsServerClient>(client.EndPoint, client));
        PeerDemux demux = _demux;
        _commands?.Post(() => demux?.Close(client));
    }

    /// <inheritdoc />
    public void OnEstablished(DtlsServerClient client)
    {
        _clients[client.EndPoint] = client;
    }

    /// <inheritdoc />
    public void OnData(DtlsServerClient client, byte[] buffer, int length)
    {
        if (!_clients.TryGetValue(client.EndPoint, out DtlsServerClient current) || current != client)
        {
            return;
        }

        try
        {
            DataReceivedEvent?.Invoke(client, buffer, length);
        }
        catch (Exception e)
        {
            Logger.Error($"Error occurred while invoking DataReceivedEvent:\n{e}");
        }
    }

    /// <inheritdoc />
    public void OnClosed(DtlsServerClient client)
    {
        // Removes the entry only if it is still this client, not a newer session on the same endpoint.
        ((ICollection<KeyValuePair<IPEndPoint, DtlsServerClient>>) _clients).Remove(
            new KeyValuePair<IPEndPoint, DtlsServerClient>(client.EndPoint, client));
    }

    /// <summary>
    /// A datagram sent to a player that has gone away makes Windows fail the next receive call on the shared
    /// socket. That error belongs to no one, so it is turned off; other platforms do not report it.
    /// </summary>
    private static void IgnoreConnectionResets(Socket socket)
    {
        try
        {
            socket.IOControl(SioUdpConnReset, [0], null);
        }
        catch (Exception e)
            when (e is SocketException || e is PlatformNotSupportedException || e is NotSupportedException)
        {
            Logger.Debug("Could not turn off connection reset reports on the server socket");
        }
    }
}
