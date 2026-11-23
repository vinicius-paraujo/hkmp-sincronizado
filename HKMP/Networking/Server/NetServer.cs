using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Game.Server.Core;
using Hkmp.Game.Server.Sessions;
using Hkmp.Logging;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Connection;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server.Lifecycle;
using Hkmp.Networking.Server.Transport;
using Hkmp.Networking.Timing;

namespace Hkmp.Networking.Server;

/// <summary>
/// Server that manages connection with clients. Each connection belongs to a session, and a client reaches the
/// session of an admitted player only through its token, never through the address it sends from.
/// </summary>
internal class NetServer : INetServer {
    /// <summary>
    /// The time to throttle a client after they were rejected connection in milliseconds.
    /// </summary>
    private const int ThrottleTime = 2500;

    /// <summary>
    /// The reason shown to a client whose session could not be taken back.
    /// </summary>
    private const string ResumptionFailedReason = "The session could not be resumed";

    /// <summary>
    /// The packet manager instance.
    /// </summary>
    private readonly PacketManager _packetManager;

    /// <summary>
    /// Underlying DTLS transport of the server.
    /// </summary>
    private readonly ServerTransport _dtlsServer;

    /// <summary>
    /// The connection of each established DTLS client. The DTLS client is the transport of one connection, so a
    /// new handshake from the same address is a new connection.
    /// </summary>
    private readonly ConcurrentDictionary<DtlsServerClient, NetServerClient> _clientsByTransport;

    /// <summary>
    /// The connection each player ID uses now.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, NetServerClient> _clientsById;

    /// <summary>
    /// The transport of each connection, to end it when the connection ends.
    /// </summary>
    private readonly ConcurrentDictionary<NetServerClient, DtlsServerClient> _transports;

    /// <summary>
    /// Dictionary for the endpoints of clients that have their connection throttled mapped to a stopwatch that
    /// keeps track of their last connection attempt. The key includes the port, because players behind the same
    /// tunnel or NAT share one address, and a rejected player must not keep the others out.
    /// </summary>
    private readonly ConcurrentDictionary<IPEndPoint, Stopwatch> _throttledClients;

    /// <summary>
    /// The sessions of the connections, which run on the core.
    /// </summary>
    private readonly SessionRegistry<NetServerClient> _sessions;

    /// <summary>
    /// Creates the timers of each client, which run on the core.
    /// </summary>
    private readonly CoreTimerFactory _timers;

    /// <summary>
    /// The deadlines of the connections and of the sessions.
    /// </summary>
    private readonly ConnectionTimeouts _timeouts;

    /// <summary>
    /// Opens and closes the port and tells the real state of the server.
    /// </summary>
    private readonly ServerLifecycle _lifecycle;

    /// <summary>
    /// The one thread that changes server state. Received data, timers and requests from other threads all run on
    /// it.
    /// </summary>
    public ServerCore Core { get; }

    /// <summary>
    /// Checks that the server API is used only on the core thread.
    /// </summary>
    public CoreThreadGuard Guard { get; }

    /// <summary>
    /// Event that is called when the session of an admitted player ends without the player leaving: it went
    /// silent and did not come back before the drop deadline, or it vanished during its admission.
    /// </summary>
    public event Action<ushort> ClientTimeoutEvent;

    /// <summary>
    /// Event that is called when a registered client stops sending updates for a few seconds.
    /// </summary>
    public event Action<ushort> ClientSilenceEvent;

    /// <summary>
    /// Event that is called when a client that went silent sends updates again.
    /// </summary>
    public event Action<ushort> ClientResumeEvent;

    /// <summary>
    /// Event that is called when a player took its session back on a new connection. The player keeps its ID; the
    /// argument tells whether the session had been suspended.
    /// </summary>
    public event Action<ushort, bool> SessionResumedEvent;

    /// <summary>
    /// Event that is called when the server shuts down.
    /// </summary>
    public event Action ShutdownEvent;

    /// <summary>
    /// Event that is called when a new client wants to connect.
    /// </summary>
    public event Action<NetServerClient, ClientInfo, ServerInfo> ConnectionRequestEvent;

    /// <summary>
    /// Event that is called when a client presents the token of a live session. The handler decides in the server
    /// info, as for a new entry; the arguments are the arriving connection, the session's player ID and the data.
    /// </summary>
    public event Action<NetServerClient, ushort, ClientInfo, ServerInfo> ResumptionRequestEvent;

    /// <summary>
    /// Called on the core whenever the lifecycle of the server publishes its state, with the reason of a failure.
    /// </summary>
    public event Action<ServerState, ServerFailure> LifecycleChanged;

    /// <summary>
    /// Whether the port is open and the server accepts players.
    /// </summary>
    public bool IsStarted => _lifecycle.State == ServerState.Open;

    /// <summary>
    /// The state of the server's lifecycle.
    /// </summary>
    public ServerState State => _lifecycle.State;

    /// <summary>
    /// Why the server failed, while it is in the failed state.
    /// </summary>
    public ServerFailure Failure => _lifecycle.Failure;

    public NetServer(PacketManager packetManager) : this(packetManager, ConnectionTimeouts.Default) {
    }

    /// <summary>
    /// Creates the server with the given deadlines for its connections and sessions.
    /// </summary>
    /// <param name="packetManager">The packet manager used on the server.</param>
    /// <param name="timeouts">The deadlines of the connections and of the sessions.</param>
    public NetServer(PacketManager packetManager, ConnectionTimeouts timeouts) {
        _packetManager = packetManager;
        _timeouts = timeouts;

        _dtlsServer = new ServerTransport(_timeouts);

        _clientsByTransport = new ConcurrentDictionary<DtlsServerClient, NetServerClient>();
        _clientsById = new ConcurrentDictionary<ushort, NetServerClient>();
        _transports = new ConcurrentDictionary<NetServerClient, DtlsServerClient>();
        _throttledClients = new ConcurrentDictionary<IPEndPoint, Stopwatch>();

        var clock = Stopwatch.StartNew();
        _sessions = new SessionRegistry<NetServerClient>(new PlayerIdPool(() => clock.Elapsed, timeouts.IdRest));

        Core = new ServerCore();
        Guard = new CoreThreadGuard(Core);
        _timers = new CoreTimerFactory(Core);

        _lifecycle = new ServerLifecycle(new TransportPort(this), EndAllSessions);
        _lifecycle.StateChanged += (state, failure) => LifecycleChanged?.Invoke(state, failure);
        _dtlsServer.LoopFailed += () => Core.Post(_lifecycle.OnLoopFailed);

        _packetManager.RegisterServerConnectionPacketHandler<ClientInfo>(
            ServerConnectionPacketId.ClientInfo,
            OnClientInfoReceived
        );
    }

    /// <summary>
    /// Starts the server on the given port. The outcome is the state of the lifecycle: open, or failed with its
    /// reason. A server that is not stopped is stopped first.
    /// </summary>
    /// <param name="port">The networking port.</param>
    public void Start(int port) {
        if (State != ServerState.Stopped) {
            Stop();
        }

        Logger.Info($"Starting NetServer on port {port}");
        _lifecycle.Start(port);
    }

    /// <summary>
    /// Whether a player with the given key is in the game, in an active or a suspended session. Runs on the core.
    /// </summary>
    /// <param name="identity">The key of the player.</param>
    /// <returns>True if such a session exists.</returns>
    public bool HasLiveSession(string identity) {
        return _sessions.HasLiveSession(identity);
    }

    /// <summary>
    /// Callback method for when data is received from the DTLS server, on the network thread. The data is handled
    /// on the core thread.
    /// </summary>
    /// <param name="dtlsServerClient">The DTLS server client from which the data was received.</param>
    /// <param name="buffer">Byte array for the buffer of data.</param>
    /// <param name="length">The number of bytes in the array that can be read.</param>
    private void OnDataReceived(DtlsServerClient dtlsServerClient, byte[] buffer, int length) {
        Core.Post(() => ProcessReceivedData(dtlsServerClient, buffer, length));
    }

    /// <summary>
    /// Handles data received from a client, on the core thread.
    /// </summary>
    /// <param name="dtlsServerClient">The DTLS server client from which the data was received.</param>
    /// <param name="buffer">Byte array for the buffer of data.</param>
    /// <param name="length">The number of bytes in the array that can be read.</param>
    private void ProcessReceivedData(DtlsServerClient dtlsServerClient, byte[] buffer, int length) {
        // Data that was queued before the server stopped belongs to no session anymore
        if (!IsStarted) {
            return;
        }

        var packets = PacketManager.HandleReceivedData(buffer, length, dtlsServerClient.Fragments);

        if (!_clientsByTransport.TryGetValue(dtlsServerClient, out var client)) {
            var endPoint = dtlsServerClient.EndPoint;
            if (IsThrottled(endPoint)) {
                return;
            }

            Logger.Info($"New connection from {endPoint.Address}:{endPoint.Port}");

            client = CreateNewClient(dtlsServerClient);
        }

        HandleClientPackets(client, packets);
    }

    /// <summary>
    /// Throttle the client at the given endpoint after a rejected or malformed connection attempt.
    /// </summary>
    /// <param name="endPoint">The endpoint of the client.</param>
    internal void Throttle(IPEndPoint endPoint) {
        _throttledClients[endPoint] = Stopwatch.StartNew();
    }

    /// <summary>
    /// Whether data from the given endpoint must be ignored because its client is throttled. Each ignored attempt
    /// restarts the throttle, so that a client that keeps trying times out instead of being let in.
    /// </summary>
    /// <param name="endPoint">The endpoint of the client.</param>
    /// <returns>True if the data must be ignored; otherwise false.</returns>
    internal bool IsThrottled(IPEndPoint endPoint) {
        if (!_throttledClients.TryGetValue(endPoint, out var clientStopwatch)) {
            return false;
        }

        if (clientStopwatch.ElapsedMilliseconds < ThrottleTime) {
            clientStopwatch.Restart();
            return true;
        }

        _throttledClients.TryRemove(endPoint, out _);
        return false;
    }

    /// <summary>
    /// Create the connection of a DTLS client that just sent its first data, with a session that is admitting.
    /// </summary>
    /// <param name="dtlsServerClient">The DTLS server client to create the client from.</param>
    /// <returns>A new net server client instance.</returns>
    internal NetServerClient CreateNewClient(DtlsServerClient dtlsServerClient) {
        var id = _sessions.TakeId();
        var netServerClient = new NetServerClient(
            dtlsServerClient.DtlsTransport, _packetManager, dtlsServerClient.EndPoint, id, _timers, _timeouts
        );
        _sessions.Open(id, netServerClient);

        netServerClient.ChunkSender.Start();

        netServerClient.ConnectionManager.ConnectionRequestEvent += OnConnectionRequest;
        netServerClient.ConnectionManager.ConnectionTimeoutEvent += () => HandleClientTimeout(netServerClient);
        netServerClient.ConnectionManager.StartAcceptingConnection();

        netServerClient.UpdateManager.TimeoutEvent += () => HandleClientTimeout(netServerClient);
        netServerClient.UpdateManager.SilenceEvent += () => OnSilence(netServerClient);
        netServerClient.UpdateManager.ResumeEvent += () => OnPacketAfterSilence(netServerClient);
        netServerClient.UpdateManager.StartUpdates();

        _clientsByTransport[dtlsServerClient] = netServerClient;
        _transports[netServerClient] = dtlsServerClient;
        _clientsById[id] = netServerClient;

        return netServerClient;
    }

    /// <summary>
    /// The connection stopped sending: an admitted player's session is suspended until it comes back.
    /// </summary>
    /// <param name="client">The connection.</param>
    private void OnSilence(NetServerClient client) {
        var session = _sessions.Of(client);
        if (session == null || !client.IsRegistered) {
            return;
        }

        if (_sessions.Handle(session, SessionEvent.Silence) == SessionAction.Suspend) {
            ClientSilenceEvent?.Invoke(client.Id);
        }
    }

    /// <summary>
    /// A packet arrived on a connection after it went silent.
    /// </summary>
    /// <param name="client">The connection.</param>
    private void OnPacketAfterSilence(NetServerClient client) {
        var session = _sessions.Of(client);
        if (session == null) {
            return;
        }

        if (_sessions.Handle(session, SessionEvent.Packet) == SessionAction.ResumeFromPacket) {
            ClientResumeEvent?.Invoke(client.Id);
        }
    }

    /// <summary>
    /// Handles a deadline of a connection: the admission took too long, or the connection sent nothing until the
    /// drop deadline. The session ends, and the server manager hears of it when the player had been admitted.
    /// </summary>
    /// <param name="client">The connection whose deadline passed.</param>
    internal void HandleClientTimeout(NetServerClient client) {
        var session = _sessions.Of(client);
        if (session == null) {
            // A connection that a resumption replaced, or one that already ended.
            EndConnection(client);
            return;
        }

        var id = client.Id;
        var sessionEvent = session.State switch {
            SessionState.Admitting or SessionState.Refusing => SessionEvent.AdmissionDeadline,
            SessionState.Suspended => SessionEvent.DropDeadline,
            _ => SessionEvent.Leave
        };
        var action = _sessions.Handle(session, sessionEvent);

        // An accepted client already exists as a player for the server manager and the addons, even while its
        // registration is still being sent, so they must hear that it left
        if (action == SessionAction.AnnounceLeave || client.IsAccepted) {
            ClientTimeoutEvent?.Invoke(id);
        }

        EndConnection(client);
        Logger.Info($"Client {id} timed out");
    }

    /// <summary>
    /// Stops a connection and ends its transport, without touching the session it may have belonged to.
    /// </summary>
    /// <param name="client">The connection.</param>
    private void EndConnection(NetServerClient client) {
        client.Disconnect();
        if (_transports.TryRemove(client, out var transport)) {
            _dtlsServer.DisconnectClient(transport);
            _clientsByTransport.TryRemove(transport, out _);
        }

        if (_clientsById.TryGetValue(client.Id, out var current) && current == client) {
            _clientsById.TryRemove(client.Id, out _);
        }
    }

    /// <summary>
    /// Handle a list of packets from a client.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="packets">The list of packets to handle.</param>
    private void HandleClientPackets(NetServerClient client, List<Packet.Packet> packets) {
        foreach (var packet in packets) {
            // Create a server update packet from the raw packet instance
            var serverUpdatePacket = new ServerUpdatePacket();
            if (!serverUpdatePacket.ReadPacket(packet)) {
                // If ReadPacket returns false, we received a malformed packet
                if (client.IsRegistered) {
                    // Since the client is registered already, we simply ignore the packet
                    continue;
                }

                // If the client is not yet registered, we log the malformed packet, and throttle the client
                Logger.Debug($"Received malformed packet from client at: {client.EndPoint}");

                // We throttle the client, because chances are that they are using an outdated version of the
                // networking protocol, and keeping connection will potentially never time them out
                Throttle(client.EndPoint);

                continue;
            }

            var packetData = serverUpdatePacket.GetPacketData();

            // Only what reaches its handler may be confirmed: before the registration there is no handler for the
            // game's data, so a packet with any is left unconfirmed, and the client sends its data again.
            var delivered = client.IsRegistered || CarriesOnlyConnectionData(packetData);
            client.UpdateManager.OnReceivePacket<ServerUpdatePacket, ServerUpdatePacketId>(serverUpdatePacket, delivered);

            // First process slice or slice ack data if it exists and pass it onto the chunk sender or chunk receiver
            if (packetData.TryGetValue(ServerUpdatePacketId.Slice, out var sliceData)) {
                packetData.Remove(ServerUpdatePacketId.Slice);
                client.ChunkReceiver.ProcessReceivedData((SliceData) sliceData);
            }

            if (packetData.TryGetValue(ServerUpdatePacketId.SliceAck, out var sliceAckData)) {
                packetData.Remove(ServerUpdatePacketId.SliceAck);
                client.ChunkSender.ProcessReceivedData((SliceAckData) sliceAckData);
            }

            // Then, if the client is registered, we let the packet manager handle the rest of the data
            if (client.IsRegistered) {
                _packetManager.HandleServerUpdatePacket(client.Id, serverUpdatePacket);
            }
        }
    }

    /// <summary>
    /// Whether the packet carries nothing but the slices of the connection phase and their acknowledgements.
    /// </summary>
    /// <param name="packetData">The data of the packet.</param>
    /// <returns>True if nothing else is in it.</returns>
    private static bool CarriesOnlyConnectionData(Dictionary<ServerUpdatePacketId, IPacketData> packetData) {
        foreach (var id in packetData.Keys) {
            if (id != ServerUpdatePacketId.Slice && id != ServerUpdatePacketId.SliceAck) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Callback method for when a connection request is received. A request with the token of a live session of
    /// the same player takes that session back; any other request is a new entry.
    /// </summary>
    /// <param name="clientId">The ID of the client.</param>
    /// <param name="clientInfo">The client info instance containing details about the client.</param>
    /// <param name="serverInfo">The server info instance that should be modified to reflect whether the client's
    /// connection is accepted or not.</param>
    internal void OnConnectionRequest(ushort clientId, ClientInfo clientInfo, ServerInfo serverInfo) {
        if (!_clientsById.TryGetValue(clientId, out var client) || _sessions.Of(client) is not { } arriving) {
            Logger.Error($"Connection request for client without known ID: {clientId}");
            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = "Unknown client";

            return;
        }

        var live = FindLiveSession(clientInfo);
        if (live != null) {
            ResumeSession(client, arriving, live, clientInfo, serverInfo);
            return;
        }

        EndSuspendedSessionOf(clientInfo.AuthKey);

        // Invoke the connection request event ourselves first, then check the result
        ConnectionRequestEvent?.Invoke(client, clientInfo, serverInfo);

        if (serverInfo.ConnectionResult == ServerConnectionResult.Accepted) {
            _sessions.Admit(arriving, clientInfo.AuthKey);
            serverInfo.SessionToken = arriving.Token.ToBytes();
            Logger.Debug($"Connection request for client ID {clientId} was accepted, session {arriving.Token}");

            Register(client);
            return;
        }

        Refuse(client, arriving, serverInfo);
    }

    /// <summary>
    /// The live session whose token the request presents, if it belongs to the same player.
    /// </summary>
    /// <param name="clientInfo">The request.</param>
    /// <returns>The session, or null for a new entry.</returns>
    private Session<NetServerClient> FindLiveSession(ClientInfo clientInfo) {
        SessionToken token;
        try {
            token = SessionToken.FromBytes(clientInfo.SessionToken);
        } catch (ArgumentException) {
            return null;
        }

        var live = _sessions.Find(token);

        // A token that names no live session is an expired one: the player simply enters again.
        if (live == null || !string.Equals(live.Identity, clientInfo.AuthKey, StringComparison.Ordinal)) {
            return null;
        }

        return live;
    }

    /// <summary>
    /// Ends the suspended session of the player with the given key, so the player can enter again at once. A game
    /// that closed or crashed sends no leave and loses its token, so without this the player would be refused as
    /// already in until the drop deadline. A session that still sends is left alone: it belongs to a game that is
    /// still running.
    /// </summary>
    /// <param name="identity">The key of the player entering.</param>
    private void EndSuspendedSessionOf(string identity) {
        var session = _sessions.OfIdentity(identity);
        if (session == null || !_clientsById.TryGetValue(session.PlayerId, out var previous)) {
            return;
        }

        if (_sessions.Handle(session, SessionEvent.Replaced) != SessionAction.AnnounceLeave) {
            return;
        }

        ClientTimeoutEvent?.Invoke(session.PlayerId);
        EndConnection(previous);
        Logger.Info($"Player {session.PlayerId} entered again without its token; its suspended session ended");
    }

    /// <summary>
    /// Moves a live session to the arriving connection, if the server manager lets the player back.
    /// </summary>
    private void ResumeSession(
        NetServerClient client,
        Session<NetServerClient> arriving,
        Session<NetServerClient> live,
        ClientInfo clientInfo,
        ServerInfo serverInfo
    ) {
        var playerId = live.PlayerId;
        ResumptionRequestEvent?.Invoke(client, playerId, clientInfo, serverInfo);
        if (serverInfo.ConnectionResult != ServerConnectionResult.Accepted) {
            Refuse(client, arriving, serverInfo);
            return;
        }

        var resumption = _sessions.Resume(live, arriving);
        if (resumption.Previous == null) {
            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = ResumptionFailedReason;
            Refuse(client, arriving, serverInfo);
            return;
        }

        // The arriving connection drops its own ID and carries the session's from now on.
        _clientsById.TryRemove(client.Id, out _);
        client.TakeOver(playerId);
        _clientsById[playerId] = client;
        EndConnection(resumption.Previous);

        // The reliable channel belongs to the session: the old connection sent its last packet when it ended, and
        // what it carried without confirmation goes on through the new one.
        client.UpdateManager.AdoptChannelFrom(resumption.Previous.UpdateManager);

        serverInfo.SessionToken = live.Token.ToBytes();
        serverInfo.Resumed = true;
        Logger.Info($"Player {playerId} took its session back from {client.EndPoint}, session {live.Token}");

        SessionResumedEvent?.Invoke(playerId, resumption.Action == SessionAction.SwapConnectionAndResume);
        Register(client);
    }

    /// <summary>
    /// Registers an accepted connection once its acceptance reached the client.
    /// </summary>
    /// <param name="client">The connection.</param>
    private void Register(NetServerClient client) {
        client.IsAccepted = true;

        // The chunk sender completes on its own thread, so the completion returns to the core
        client.ConnectionManager.FinishConnection(() => Core.Post(() => {
            Logger.Debug("Connection has finished sending data, registering client");

            client.IsRegistered = true;
            client.ConnectionManager.StopAcceptingConnection();
        }));
    }

    /// <summary>
    /// Refuses a connection: the reason is sent, and the connection ends once the client confirmed it or the
    /// admission deadline passed.
    /// </summary>
    private void Refuse(NetServerClient client, Session<NetServerClient> arriving, ServerInfo serverInfo) {
        Logger.Debug($"Connection request for client ID {client.Id} was rejected: {serverInfo.ConnectionRejectedMessage}");
        _sessions.Handle(arriving, SessionEvent.Refused);

        client.ConnectionManager.FinishConnection(() => Core.Post(() => {
            Logger.Debug("Connection has finished sending data, disconnecting client and throttling");

            if (_sessions.Of(client) is { } session) {
                _sessions.Handle(session, SessionEvent.ResponseDelivered);
            }

            EndConnection(client);

            if (serverInfo.ConnectionRejectedMessage != ConnectionRejection.UsernameInUse) {
                Throttle(client.EndPoint);
            }
        }));
    }

    /// <summary>
    /// Callback method for when client info is received in a connection packet.
    /// </summary>
    /// <param name="clientId">The ID of the client that sent the client info.</param>
    /// <param name="clientInfo">The client info instance.</param>
    private void OnClientInfoReceived(ushort clientId, ClientInfo clientInfo) {
        if (!_clientsById.TryGetValue(clientId, out var client)) {
            Logger.Error($"ClientInfo received from client without known ID: {clientId}");
            return;
        }

        client.ConnectionManager.ProcessClientInfo(clientInfo);
    }

    /// <summary>
    /// Stops the server from any state; it is stopped when this returns.
    /// </summary>
    public void Stop() {
        Logger.Info("Stopping NetServer");
        _lifecycle.Stop();

        if (Guard.Violations > 0) {
            Logger.Warn($"Server thread violations while running: {Guard.Violations}");
        }

        // Invoke the shutdown event to notify all registered parties of the shutdown
        ShutdownEvent?.Invoke();
    }

    /// <summary>
    /// Ends the connection of every client and forgets every session, as the server goes away.
    /// </summary>
    private void EndAllSessions() {
        foreach (var client in _clientsByTransport.Values) {
            client.Disconnect();
        }

        _clientsByTransport.Clear();
        _clientsById.Clear();
        _transports.Clear();
        _sessions.Clear();
    }

    /// <summary>
    /// The transport's socket and network thread, as the lifecycle opens and closes them.
    /// </summary>
    private sealed class TransportPort : IServerPort {
        private readonly NetServer _server;

        public TransportPort(NetServer server) {
            _server = server;
        }

        public void Open(int port) {
            _server._dtlsServer.Start(port);
            _server._dtlsServer.DataReceivedEvent += _server.OnDataReceived;
        }

        public bool Close() {
            _server._dtlsServer.DataReceivedEvent -= _server.OnDataReceived;
            _server._throttledClients.Clear();
            return _server._dtlsServer.Stop();
        }
    }

    /// <summary>
    /// Callback method for when a client disconnects from the server.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    public void OnClientDisconnect(ushort id) {
        if (!_clientsById.TryGetValue(id, out var client)) {
            Logger.Warn($"Handling disconnect from ID {id}, but there's no matching client");
            return;
        }

        if (_sessions.Of(client) is { } session) {
            _sessions.Handle(session, SessionEvent.Leave);
        }

        EndConnection(client);

        Logger.Info($"Client {id} disconnected");
    }

    /// <summary>
    /// Get the update manager for the client with the given ID.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    /// <returns>The update manager for the client, or null if there does not exist a client with the
    /// given ID.</returns>
    public ServerUpdateManager GetUpdateManagerForClient(ushort id) {
        if (!_clientsById.TryGetValue(id, out var netServerClient)) {
            return null;
        }

        return netServerClient.UpdateManager;
    }

    /// <summary>
    /// The round trip time of every connected client, in milliseconds.
    /// </summary>
    /// <returns>The id and round trip time of each client.</returns>
    public List<(ushort Id, ushort Rtt)> PingsOfAllClients() {
        var pings = new List<(ushort, ushort)>();
        foreach (var pair in _clientsById) {
            var rtt = System.Math.Min(System.Math.Max(pair.Value.UpdateManager.AverageRtt, 0), ushort.MaxValue);
            pings.Add((pair.Key, (ushort) rtt));
        }

        return pings;
    }

    /// <summary>
    /// Execute a given action for the update manager of all connected clients.
    /// </summary>
    /// <param name="dataAction">The action to execute with each update manager.</param>
    public void SetDataForAllClients(Action<ServerUpdateManager> dataAction) {
        foreach (var netServerClient in _clientsById.Values) {
            dataAction(netServerClient.UpdateManager);
        }
    }

    /// <inheritdoc />
    public IServerAddonNetworkSender<TPacketId> GetNetworkSender<TPacketId>(
        ServerAddon addon
    ) where TPacketId : Enum {
        if (addon == null) {
            throw new ArgumentException("Parameter 'addon' cannot be null");
        }

        // Check whether this addon has actually requested network access through their property
        // We check this otherwise an ID has not been assigned and it can't send network data
        if (!addon.NeedsNetwork) {
            throw new InvalidOperationException("Addon has not requested network access through property");
        }

        // Check whether there already is a network sender for the given addon
        if (addon.NetworkSender != null) {
            if (!(addon.NetworkSender is IServerAddonNetworkSender<TPacketId> addonNetworkSender)) {
                throw new InvalidOperationException(
                    "Cannot request network senders with differing generic parameters");
            }

            return addonNetworkSender;
        }

        // Otherwise create one, store it and return it
        var newAddonNetworkSender = new ServerAddonNetworkSender<TPacketId>(this, addon);
        addon.NetworkSender = newAddonNetworkSender;

        return newAddonNetworkSender;
    }

    /// <inheritdoc />
    public IServerAddonNetworkReceiver<TPacketId> GetNetworkReceiver<TPacketId>(
        ServerAddon addon,
        Func<TPacketId, IPacketData> packetInstantiator
    ) where TPacketId : Enum {
        if (addon == null) {
            throw new ArgumentException("Parameter 'addon' cannot be null");
        }

        if (packetInstantiator == null) {
            throw new ArgumentException("Parameter 'packetInstantiator' cannot be null");
        }

        // Check whether this addon has actually requested network access through their property
        // We check this otherwise an ID has not been assigned and it can't send network data
        if (!addon.NeedsNetwork) {
            throw new InvalidOperationException("Addon has not requested network access through property");
        }

        if (!addon.Id.HasValue) {
            throw new InvalidOperationException("Addon has no ID assigned");
        }

        ServerAddonNetworkReceiver<TPacketId> networkReceiver = null;

        // Check whether an existing network receiver exists
        if (addon.NetworkReceiver == null) {
            networkReceiver = new ServerAddonNetworkReceiver<TPacketId>(addon, _packetManager);
            addon.NetworkReceiver = networkReceiver;
        } else if (!(addon.NetworkReceiver is IServerAddonNetworkReceiver<TPacketId>)) {
            throw new InvalidOperationException(
                "Cannot request network receivers with differing generic parameters");
        }

        // After we know that this call did not use a different generic, we can update packet info
        ServerUpdatePacket.AddonPacketInfoDict[addon.Id.Value] = new AddonPacketInfo(
            // Transform the packet instantiator function from a TPacketId as parameter to byte
            networkReceiver?.TransformPacketInstantiator(packetInstantiator),
            (byte) Enum.GetValues(typeof(TPacketId)).Length
        );

        return addon.NetworkReceiver as IServerAddonNetworkReceiver<TPacketId>;
    }
}
