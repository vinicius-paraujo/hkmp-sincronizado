using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Logging;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Client.Connection;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Timing;

namespace Hkmp.Networking.Client;

/// <summary>
/// The networking client that manages the UDP client for sending and receiving data. The state of the connection
/// lives in a <see cref="ClientConnection"/> on the main thread; this class is its transport, and turns its changes
/// into the events the game listens to, all raised on the main thread.
/// </summary>
internal class NetClient : INetClient, IClientConnectionPort {
    /// <summary>
    /// The packet manager instance.
    /// </summary>
    private readonly PacketManager _packetManager;

    /// <summary>
    /// The deadlines of the connection.
    /// </summary>
    private readonly ConnectionTimeouts _timeouts;

    /// <summary>
    /// The client update manager for this net client.
    /// </summary>
    public ClientUpdateManager UpdateManager { get; }

    /// <summary>
    /// Event that is called when the server admits the client, or takes its session back.
    /// </summary>
    public event Action<ServerInfo> ConnectEvent;

    /// <summary>
    /// Event that is called when the client fails to connect to a server.
    /// </summary>
    public event Action<ConnectionFailedResult> ConnectFailedEvent;

    /// <summary>
    /// Event that is called when the client disconnects from a server.
    /// </summary>
    public event Action DisconnectEvent;

    /// <summary>
    /// Event that is called when a connection that was up is lost for good: the server ended it, or it could not
    /// be taken back in time.
    /// </summary>
    public event Action TimeoutEvent;

    /// <summary>
    /// Event that is called when the server goes silent and the client starts taking its session back.
    /// </summary>
    public event Action StalledEvent;

    /// <summary>
    /// Event that is called when the silent connection answers again before it was replaced.
    /// </summary>
    public event Action ResumedEvent;

    /// <summary>
    /// Event that is called when an attempt to take the session back replaces the silent connection, so everything
    /// tied to the old connection must be dropped before the server answers.
    /// </summary>
    public event Action TransportReplacedEvent;

    /// <summary>
    /// Event that is called after every change of the connection's state.
    /// </summary>
    public event Action<ConnectionState> StateChangedEvent;

    /// <summary>
    /// The state of the connection.
    /// </summary>
    public ConnectionState State => _connection.State;

    /// <summary>
    /// How many attempts started since the connection went silent.
    /// </summary>
    public int ResumeAttempts => _connection.ResumeAttempts;

    /// <summary>
    /// The connection status of the client, as the parts of the game that predate the connection's states read it.
    /// A connection that is taking its session back still counts as connected, so the game keeps queueing its data.
    /// </summary>
    public ClientConnectionStatus ConnectionStatus => State switch {
        ConnectionState.Connecting or ConnectionState.Admitting => ClientConnectionStatus.Connecting,
        ConnectionState.Connected or ConnectionState.Resuming => ClientConnectionStatus.Connected,
        _ => ClientConnectionStatus.NotConnected
    };

    /// <inheritdoc />
    public bool IsConnected => ConnectionStatus == ClientConnectionStatus.Connected;

    /// <summary>
    /// Chunk sender instance for sending large amounts of data.
    /// </summary>
    private readonly ClientChunkSender _chunkSender;
    /// <summary>
    /// Chunk receiver instance for receiving large amounts of data.
    /// </summary>
    private readonly ClientChunkReceiver _chunkReceiver;

    /// <summary>
    /// The client connection manager responsible for handling sending and receiving connection data.
    /// </summary>
    private readonly ClientConnectionManager _connectionManager;

    /// <summary>
    /// The state of the connection.
    /// </summary>
    private readonly ClientConnection _connection;

    /// <summary>
    /// The DTLS client of the current connection. Each attempt opens a new one, so a cancelled attempt that ends late
    /// can only close its own.
    /// </summary>
    private DtlsClient _dtlsClient;

    /// <summary>
    /// The fragments received from the server so far.
    /// </summary>
    private PacketFragments _fragments = new PacketFragments();

    /// <summary>
    /// The update packets received after the connection was accepted and before the handlers for them exist.
    /// </summary>
    private readonly HeldUpdates<ClientUpdatePacket> _heldUpdates = new HeldUpdates<ClientUpdatePacket>();

    /// <summary>
    /// Whether the server accepted the current connection, as the receive thread sees it. The state of the connection
    /// only changes on the main thread, a moment later, and the packets in between must already count as delivered or
    /// held.
    /// </summary>
    private volatile bool _accepted;

    /// <summary>
    /// Whether the current connection replaces a silent one of the same session and the server has not answered yet.
    /// Until it answers, the server talks through a channel of its own, whose numbering is not the session's, so
    /// nothing that arrives may touch the session's channel; only the slices of the answer are read.
    /// </summary>
    private volatile bool _replacing;

    private string _address;
    private int _port;
    private string _username;
    private string _authKey;
    private List<AddonData> _addonData;
    private bool _keepSession;

    /// <summary>
    /// Forgets the session token, so the next connection enters as a new player instead of taking a session back.
    /// </summary>
    public void ForgetSession() {
        _keepSession = false;
    }

    /// <summary>
    /// Construct the net client with the given packet manager.
    /// </summary>
    /// <param name="packetManager">The packet manager instance.</param>
    public NetClient(PacketManager packetManager) : this(packetManager, ConnectionTimeouts.Default, new Random().NextDouble) {
    }

    /// <summary>
    /// Construct the net client with the given packet manager, deadlines and source of the random delay.
    /// </summary>
    /// <param name="packetManager">The packet manager instance.</param>
    /// <param name="timeouts">The deadlines of the connection.</param>
    /// <param name="random">Returns a number from zero up to, but not including, one.</param>
    public NetClient(PacketManager packetManager, ConnectionTimeouts timeouts, Func<double> random) {
        _packetManager = packetManager;
        _timeouts = timeouts;

        UpdateManager = new ClientUpdateManager(timeouts);
        var mailbox = MainThreadMailbox.Shared;
        UpdateManager.SilenceEvent += () => mailbox.Post(_connection.OnSilence);
        UpdateManager.ResumeEvent += () => mailbox.Post(_connection.OnPacketAfterSilence);

        _chunkSender = new ClientChunkSender(UpdateManager);
        _chunkReceiver = new ClientChunkReceiver(UpdateManager);
        _connectionManager = new ClientConnectionManager(_packetManager, _chunkSender, _chunkReceiver);
        _connectionManager.ServerInfoReceivedEvent += OnServerInfoReceived;

        var clock = Stopwatch.StartNew();
        _connection = new ClientConnection(
            this,
            timeouts,
            new ReconnectPolicy(timeouts, random),
            () => clock.Elapsed,
            mailbox
        );
        _connection.Transitioned += OnTransitioned;
    }

    /// <summary>
    /// Starts establishing a connection with the given host on the given port.
    /// </summary>
    /// <param name="address">The address of the host to connect to.</param>
    /// <param name="port">The port of the host to connect to.</param>
    /// <param name="username">The username of the client.</param>
    /// <param name="authKey">The auth key of the client.</param>
    /// <param name="addonData">A list of addon data that the client has.</param>
    public void Connect(
        string address,
        int port,
        string username,
        string authKey,
        List<AddonData> addonData
    ) {
        Logger.Debug($"Trying to connect NetClient to '{address}:{port}'");
        _address = address;
        _port = port;
        _username = username;
        _authKey = authKey;
        _addonData = addonData;

        _connection.Connect(_keepSession);
        _keepSession = true;
    }

    /// <summary>
    /// Leaves the server: a connected client tells it, any attempt is cancelled and a failure is acknowledged.
    /// </summary>
    public void Disconnect() {
        _connection.Leave();
    }

    /// <inheritdoc />
    public ConnectAttempt BeginHandshake(Action succeeded, Action<Exception> failed) {
        if (_dtlsClient != null) {
            CloseTransport();
            if (State == ConnectionState.Resuming) {
                TransportReplacedEvent?.Invoke();
            }
        }

        var dtlsClient = new DtlsClient(_timeouts.ClientHandshake);
        dtlsClient.DataReceivedEvent += OnReceiveData;
        _dtlsClient = dtlsClient;
        _fragments = new PacketFragments();

        return ConnectAttempt.Start(
            () => dtlsClient.Connect(_address, _port),
            dtlsClient.Disconnect,
            MainThreadMailbox.Shared,
            () => {
                UpdateManager.DtlsTransport = dtlsClient.DtlsTransport;

                // The reliable channel belongs to the session: a connection that takes the session back carries on
                // with it, while a new entry starts one.
                if (State == ConnectionState.Resuming) {
                    _replacing = true;
                    UpdateManager.ResumeUpdates();
                } else {
                    UpdateManager.StartUpdates();
                }

                _chunkSender.Start();
                succeeded();
            },
            error => {
                Logger.Info($"Connection attempt failed: {error.GetType().Name}: {error.Message}");
                failed(error);
            }
        );
    }

    /// <inheritdoc />
    public void SendClientInfo(byte[] sessionToken) {
        Logger.Debug("Starting connection with connection manager");
        _connectionManager.StartConnection(_username, _authKey, _addonData, sessionToken);
    }

    /// <inheritdoc />
    public void SendLeave() {
        Logger.Info("Sending PlayerDisconnect packet");
        UpdateManager.SetPlayerDisconnect();
    }

    /// <inheritdoc />
    public void Close() {
        CloseTransport();
        _dtlsClient = null;
    }

    /// <summary>
    /// Stops the updates and channels of the current connection and closes its socket.
    /// </summary>
    private void CloseTransport() {
        _accepted = false;
        _replacing = false;
        UpdateManager.StopUpdates();
        _chunkSender.Stop();
        _chunkReceiver.Reset();
        _dtlsClient?.Disconnect();
        _heldUpdates.Clear();

        // Clear all client addon packet handlers, because their IDs become invalid
        _packetManager.ClearClientAddonUpdatePacketHandlers();
    }

    /// <summary>
    /// Turns a change of the connection's state into the events of the game. Runs on the main thread.
    /// </summary>
    private void OnTransitioned(ConnectionState previous, ClientConnectionTransition transition) {
        Logger.Info($"Connection: {previous} -> {transition.Next} ({transition.Action})");

        switch (transition.Action) {
            case ClientConnectionAction.Admit:
            case ClientConnectionAction.Resume:
            case ClientConnectionAction.Reenter:
                try {
                    ConnectEvent?.Invoke(_connection.LastAnswer);
                } finally {
                    var released = _heldUpdates.Release(_packetManager.HandleClientUpdatePacket);
                    if (released > 0) Logger.Debug($"Handed over {released} update packets held until the handlers were registered");
                }
                break;
            case ClientConnectionAction.BeginResuming:
                StalledEvent?.Invoke();
                break;
            case ClientConnectionAction.EndResumingOnOldConnection:
                ResumedEvent?.Invoke();
                break;
        }

        // The state goes out before the failure and the leave: their handlers may leave the connection, and that
        // change must reach listeners after this one.
        StateChangedEvent?.Invoke(transition.Next);

        if (transition.Next == ConnectionState.Failed) {
            if (previous is ConnectionState.Connecting or ConnectionState.Admitting) {
                ConnectFailedEvent?.Invoke(_connection.Failure);
            } else {
                TimeoutEvent?.Invoke();
            }
        }

        if (transition.Next == ConnectionState.Disconnected) {
            DisconnectEvent?.Invoke();
        }
    }

    /// <summary>
    /// Callback method for when the DTLS client receives data. This will update the update manager that we have
    /// received data, handle packet creation from raw data, handle login responses, and forward received packets to
    /// the packet manager.
    /// </summary>
    /// <param name="buffer">Byte array containing the received bytes.</param>
    /// <param name="length">The number of bytes in the <paramref name="buffer"/>.</param>
    private void OnReceiveData(byte[] buffer, int length) {
        var packets = PacketManager.HandleReceivedData(buffer, length, _fragments);

        foreach (var packet in packets) {
            // Create a ClientUpdatePacket from the raw packet instance,
            // and read the values into it
            var clientUpdatePacket = new ClientUpdatePacket();
            if (!clientUpdatePacket.ReadPacket(packet)) {
                // If ReadPacket returns false, we received a malformed packet, which we simply ignore for now
                continue;
            }

            var packetData = clientUpdatePacket.GetPacketData();

            // Only what reaches its handler may be confirmed: before the connection is accepted there is no handler
            // for the game's data, so a packet with any is left unconfirmed, and the server sends its data again.
            var delivered = _accepted || (!_replacing && CarriesOnlyConnectionData(packetData));
            UpdateManager.OnReceivePacket<ClientUpdatePacket, ClientUpdatePacketId>(clientUpdatePacket, delivered);

            // First check for slice or slice ack data and handle it separately by passing it onto either the chunk
            // sender or chunk receiver
            if (packetData.TryGetValue(ClientUpdatePacketId.Slice, out var sliceData)) {
                packetData.Remove(ClientUpdatePacketId.Slice);
                _chunkReceiver.ProcessReceivedData((SliceData) sliceData);
            }

            if (packetData.TryGetValue(ClientUpdatePacketId.SliceAck, out var sliceAckData)) {
                packetData.Remove(ClientUpdatePacketId.SliceAck);
                _chunkSender.ProcessReceivedData((SliceAckData) sliceAckData);
            }

            // Then, once the server accepted the connection, we let the packet manager handle the rest of the packet
            // data
            if (_accepted && !_heldUpdates.Keep(clientUpdatePacket)) {
                _packetManager.HandleClientUpdatePacket(clientUpdatePacket);
            }
        }
    }

    /// <summary>
    /// Whether the packet carries nothing but the slices of the connection phase and their acknowledgements.
    /// </summary>
    /// <param name="packetData">The data of the packet.</param>
    /// <returns>True if nothing else is in it.</returns>
    private static bool CarriesOnlyConnectionData(Dictionary<ClientUpdatePacketId, IPacketData> packetData) {
        foreach (var id in packetData.Keys) {
            if (id != ClientUpdatePacketId.Slice && id != ClientUpdatePacketId.SliceAck) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Callback for the server's answer to the admission, on the receive thread. An accepted connection holds the
    /// update packets that follow until the main thread has registered their handlers; the answer itself goes to the
    /// connection on the main thread.
    /// </summary>
    private void OnServerInfoReceived(ServerInfo serverInfo) {
        Logger.Debug($"ServerInfo received: {serverInfo.ConnectionResult}, resumed: {serverInfo.Resumed}");

        // An answer to an attempt that was already given up must not open the connection that replaced it.
        if (!_connection.AwaitingAnswer) {
            Logger.Info("Ignoring an answer to a connection attempt that was given up");
            return;
        }

        if (serverInfo.ConnectionResult == ServerConnectionResult.Accepted) {
            // A session the server no longer had was not taken back: the server opened a new one with a channel of
            // its own. The old session's channel is dropped before any packet of the new one is confirmed, so none
            // of them is counted against the old record.
            if (_replacing && !serverInfo.Resumed) {
                UpdateManager.ForgetSession();
            }

            _replacing = false;
            _heldUpdates.Hold();
            _accepted = true;
        }

        MainThreadMailbox.Shared.Post(() => _connection.OnAnswer(serverInfo));
    }

    /// <inheritdoc />
    public IClientAddonNetworkSender<TPacketId> GetNetworkSender<TPacketId>(
        ClientAddon addon
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
            if (!(addon.NetworkSender is IClientAddonNetworkSender<TPacketId> addonNetworkSender)) {
                throw new InvalidOperationException(
                    "Cannot request network senders with differing generic parameters");
            }

            return addonNetworkSender;
        }

        // Otherwise create one, store it and return it
        var newAddonNetworkSender = new ClientAddonNetworkSender<TPacketId>(this, addon);
        addon.NetworkSender = newAddonNetworkSender;

        return newAddonNetworkSender;
    }

    /// <inheritdoc />
    public IClientAddonNetworkReceiver<TPacketId> GetNetworkReceiver<TPacketId>(
        ClientAddon addon,
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

        ClientAddonNetworkReceiver<TPacketId> networkReceiver = null;

        // Check whether an existing network receiver exists
        if (addon.NetworkReceiver == null) {
            networkReceiver = new ClientAddonNetworkReceiver<TPacketId>(addon, _packetManager);
            addon.NetworkReceiver = networkReceiver;
        } else if (!(addon.NetworkReceiver is IClientAddonNetworkReceiver<TPacketId>)) {
            throw new InvalidOperationException(
                "Cannot request network receivers with differing generic parameters");
        }

        networkReceiver?.AssignAddonPacketInfo(packetInstantiator);

        return addon.NetworkReceiver as IClientAddonNetworkReceiver<TPacketId>;
    }
}
