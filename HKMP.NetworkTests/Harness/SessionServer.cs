using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Hkmp.Game.Server.Sessions;
using Hkmp.Game.Settings;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Packet.Update;
using Hkmp.Networking.Server;
using Hkmp.Networking.Timing;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// The real network server of HKMP on a free loopback port. It decides on every player with the real admission
/// policy, on a server without bans or whitelist, and records which player each chat message came from.
/// </summary>
internal sealed class SessionServer : IDisposable {
    /// <summary>
    /// Deadlines in the same relations as the game's, short enough for a test to wait them out.
    /// </summary>
    public static ConnectionTimeouts ShortTimeouts { get; } = new(
        admission: TimeSpan.FromSeconds(5),
        silence: TimeSpan.FromSeconds(1),
        drop: TimeSpan.FromSeconds(3),
        idRest: TimeSpan.FromSeconds(6),
        serverHandshake: TimeSpan.FromSeconds(4),
        clientHandshake: TimeSpan.FromSeconds(5),
        retryJitter: TimeSpan.FromSeconds(0.6),
        retryFirst: TimeSpan.FromSeconds(0.2),
        retryGrowth: 1.5,
        retryMax: TimeSpan.FromSeconds(0.5),
        giveUp: TimeSpan.FromSeconds(20)
    );

    private readonly NetServer _server;
    private readonly AdmissionPolicy _admission;
    private readonly ConcurrentQueue<(ushort Player, string Message)> _messages = new();
    private readonly ConcurrentDictionary<ushort, string> _names = new();

    public int Port { get; } = Hkmp.FreeUdpPort();

    /// <summary>
    /// The deadlines the server runs with.
    /// </summary>
    public ConnectionTimeouts Timeouts { get; }

    /// <summary>
    /// The players whose session ended without them leaving.
    /// </summary>
    public ConcurrentQueue<ushort> TimedOut { get; } = new();

    /// <summary>
    /// The players whose session went silent.
    /// </summary>
    public ConcurrentQueue<ushort> Silenced { get; } = new();

    /// <summary>
    /// The players that took their session back.
    /// </summary>
    public ConcurrentQueue<ushort> Resumed { get; } = new();

    public SessionServer() : this(ConnectionTimeouts.Default) {
    }

    public SessionServer(ConnectionTimeouts timeouts) {
        Timeouts = timeouts;
        _admission = new AdmissionPolicy(new OpenServerRules(this));

        var packetManager = new PacketManager();
        _server = new NetServer(packetManager, timeouts);
        packetManager.RegisterServerUpdatePacketHandler<ChatMessage>(
            ServerUpdatePacketId.ChatMessage,
            (id, chat) => _messages.Enqueue((id, chat.Message))
        );
        _server.ConnectionRequestEvent += (client, clientInfo, serverInfo) => {
            var decision = _admission.DecideEntry(RequestOf(client, clientInfo));
            Answer(serverInfo, decision);
            if (decision.Accepted) _names[client.Id] = clientInfo.Username;
        };
        _server.ResumptionRequestEvent += (client, _, clientInfo, serverInfo) =>
            Answer(serverInfo, _admission.DecideResumption(RequestOf(client, clientInfo)));
        _server.ClientTimeoutEvent += id => {
            _names.TryRemove(id, out _);
            TimedOut.Enqueue(id);
        };
        _server.ClientSilenceEvent += Silenced.Enqueue;
        _server.SessionResumedEvent += (id, _) => Resumed.Enqueue(id);
        _server.Core.Invoke(() => _server.Start(Port));
    }

    /// <summary>
    /// Waits for a chat message with the text and returns the player it came from, or null.
    /// </summary>
    public ushort? WaitFor(string message, TimeSpan timeout) {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout) {
            foreach (var (player, text) in _messages) {
                if (text == message) return player;
            }

            Thread.Sleep(20);
        }

        return null;
    }

    /// <summary>
    /// How many times a chat message with the text reached the server.
    /// </summary>
    public int CountOf(string message) => _messages.Count(entry => entry.Message == message);

    /// <summary>
    /// Queues a chat message, reliable data, for the player's current connection; while the player is silent that is
    /// still the old one.
    /// </summary>
    public void SendChat(ushort player, string message) {
        _server.Core.Invoke(() => _server.GetUpdateManagerForClient(player)?.AddChatMessage(message));
    }

    /// <summary>
    /// Waits until the queue holds the player, and tells whether it did in time.
    /// </summary>
    public static bool WaitUntilListed(ConcurrentQueue<ushort> queue, ushort player, TimeSpan timeout) {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout) {
            if (queue.Contains(player)) return true;
            Thread.Sleep(20);
        }

        return false;
    }

    private static AdmissionRequest RequestOf(NetServerClient client, ClientInfo clientInfo) {
        return new AdmissionRequest(
            clientInfo.ProtocolVersion, clientInfo.AuthKey, clientInfo.Username, client.EndPoint.Address.ToString()
        );
    }

    private static void Answer(ServerInfo info, AdmissionDecision decision) {
        if (!decision.Accepted) {
            info.ConnectionResult = ServerConnectionResult.RejectedOther;
            info.ConnectionRejectedMessage = decision.Reason;
            return;
        }

        info.ConnectionResult = ServerConnectionResult.Accepted;
        info.AddonOrder = [];
        info.ServerSettingsUpdate = new ServerSettingsUpdate { ServerSettings = new ServerSettings() };
        info.CurrentSave = new CurrentSave();
        info.PlayerInfo = new List<(ushort, string)>();
    }

    public void Dispose() => _server.Core.Invoke(_server.Stop);

    /// <summary>
    /// A server without bans or whitelist, where the names in use are those of the players admitted and not gone.
    /// </summary>
    private sealed class OpenServerRules : IAdmissionRules {
        private readonly SessionServer _owner;

        public OpenServerRules(SessionServer owner) {
            _owner = owner;
        }

        public bool IsKeyBanned(string identity) => false;

        public bool IsAddressBanned(string address) => false;

        public bool WhitelistEnabled => false;

        public bool IsWhitelisted(string identity) => true;

        public bool IsPreListed(string username) => false;

        public bool IsNameInUse(string username) => _owner._names.Values.Contains(username);

        public bool HasLiveSession(string identity) => _owner._server.HasLiveSession(identity);
    }
}
