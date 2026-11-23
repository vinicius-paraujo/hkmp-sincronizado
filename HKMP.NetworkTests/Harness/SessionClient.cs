using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Hkmp.Networking.Timing;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Connection;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// A client that enters the server as the game does, through the real DTLS client, update manager, chunk channels
/// and connection manager, without the game around them. Each <see cref="Connect"/> opens a new socket, as a game
/// does on every connection, so it is a new connection with its own source port.
/// </summary>
internal sealed class SessionClient : IDisposable {
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    private readonly string _username;
    private readonly string _authKey;
    private readonly object _lock = new();

    private DtlsClient _dtls;
    private ClientUpdateManager _updates;
    private ClientChunkSender _chunkSender;
    private ClientChunkReceiver _chunkReceiver;
    private PacketFragments _fragments;
    private ServerInfo _answer;
    private bool _accepted;
    private bool _replacing;

    public SessionClient(string username, string authKey) {
        _username = username;
        _authKey = authKey;
    }

    /// <summary>
    /// The token of the session the server gave this client, presented on the next connection.
    /// </summary>
    public byte[] Token { get; private set; } = [];

    /// <summary>
    /// The local endpoint of the current connection's socket.
    /// </summary>
    public System.Net.IPEndPoint LocalEndPoint =>
        (System.Net.IPEndPoint) ((System.Net.Sockets.Socket) Hkmp.Field(_dtls, "_socket"))?.LocalEndPoint;

    /// <summary>
    /// Opens a new connection and sends the client's data, with the token of its session if it has one. Returns the
    /// server's answer, or null when none arrived in time.
    /// </summary>
    public ServerInfo Connect(int port) => Open(port, resume: false);

    /// <summary>
    /// Takes the session back over a new connection, as the game does after a silence: the reliable channel of the
    /// session carries on, and until the server answers nothing that arrives touches it.
    /// </summary>
    public ServerInfo Resume(int port) => Open(port, resume: true);

    /// <summary>
    /// The chat messages the server sent, in the order they reached this client, each as often as it arrived.
    /// </summary>
    public ConcurrentQueue<string> Heard { get; } = new();

    private ServerInfo Open(int port, bool resume) {
        Close();

        var answered = new ManualResetEventSlim();
        _dtls = new DtlsClient(ConnectionTimeouts.Default.ClientHandshake);
        if (!resume || _updates == null) {
            _updates = new ClientUpdateManager();
        }

        _chunkSender = new ClientChunkSender(_updates);
        _chunkReceiver = new ClientChunkReceiver(_updates);
        _fragments = new PacketFragments();
        lock (_lock) {
            _accepted = false;
            _replacing = resume;
        }

        // The packet manager hands connection data to the game's main thread, which does not exist here, so the
        // answer is read straight from the chunk.
        _chunkReceiver.ChunkReceivedEvent += chunk => {
            var connectionPacket = new ClientConnectionPacket();
            if (!connectionPacket.ReadPacket(chunk)) return;
            if (!connectionPacket.GetPacketData().TryGetValue(ClientConnectionPacketId.ServerInfo, out var data)) return;

            var info = (ServerInfo) data;
            lock (_lock) {
                _answer = info;
                _accepted = info.ConnectionResult == Networking.ServerConnectionResult.Accepted;
                if (_accepted) {
                    if (_replacing && !info.Resumed) _updates.ForgetSession();
                    _replacing = false;
                    Token = info.SessionToken;
                }
            }

            answered.Set();
        };
        _dtls.DataReceivedEvent += Receive;

        _dtls.Connect("127.0.0.1", port);
        _updates.DtlsTransport = _dtls.DtlsTransport;
        if (resume) {
            _updates.ResumeUpdates();
        } else {
            _updates.StartUpdates();
        }

        _chunkSender.Start();

        var request = new ServerConnectionPacket();
        request.SetSendingPacketData(ServerConnectionPacketId.ClientInfo, new ClientInfo {
            Username = _username,
            AuthKey = _authKey,
            AddonData = new List<AddonData>(),
            SessionToken = Token
        });
        var raw = new Networking.Packet.Packet();
        request.CreatePacket(raw);
        _chunkSender.EnqueuePacket(raw);

        return answered.Wait(AnswerTimeout) ? _answer : null;
    }

    /// <summary>
    /// Sends a chat message, which is reliable data the server hands to its handler once the client is registered.
    /// </summary>
    public void Say(string message) {
        _updates.SetChatMessage(message);
    }

    private void Receive(byte[] buffer, int length) {
        foreach (var packet in PacketManager.HandleReceivedData(buffer, length, _fragments)) {
            var update = new ClientUpdatePacket();
            if (!update.ReadPacket(packet)) continue;

            bool replacing;
            lock (_lock) replacing = _replacing;

            // As in the game, a connection that replaces a silent one keeps the session's channel out of the
            // connection phase.
            _updates.OnReceivePacket<ClientUpdatePacket, ClientUpdatePacketId>(update, !replacing);
            var data = update.GetPacketData();
            if (data.TryGetValue(ClientUpdatePacketId.Slice, out var slice)) {
                _chunkReceiver.ProcessReceivedData((SliceData) slice);
            }

            if (data.TryGetValue(ClientUpdatePacketId.SliceAck, out var sliceAck)) {
                _chunkSender.ProcessReceivedData((SliceAckData) sliceAck);
            }

            if (!replacing) {
                Hear(data.TryGetValue(ClientUpdatePacketId.ChatMessage, out var chat) ? chat : null);
                foreach (var (id, resent) in update.GetEarlierResentData()) {
                    if (id == ClientUpdatePacketId.ChatMessage) Hear(resent);
                }
            }
        }
    }

    private void Hear(IPacketData data) {
        if (data is PacketDataCollection<ChatMessage> messages) {
            foreach (var message in messages.DataInstances) Heard.Enqueue(((ChatMessage) message).Message);
        }
    }

    /// <summary>
    /// Ends the current connection without telling the server, as a game that loses its network does.
    /// </summary>
    public void Close() {
        if (_dtls == null) return;

        _updates.StopUpdates();
        _chunkSender.Stop();
        _dtls.Disconnect();
        _dtls = null;
    }

    public void Dispose() => Close();
}
