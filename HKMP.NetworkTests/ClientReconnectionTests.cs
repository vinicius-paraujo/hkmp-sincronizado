using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Hkmp.Api.Client;
using Hkmp.Networking.Client;
using Hkmp.Networking.Client.Connection;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.NetworkTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Hkmp.NetworkTests;

/// <summary>
/// The game's own network client taking its session back. The test plays the main thread: it drains the mailbox the
/// client posts to, as the game does once a frame.
/// </summary>
public class ClientReconnectionTests {
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _output;

    public ClientReconnectionTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public void Three_players_silenced_together_come_back_with_their_attempts_spread_by_the_random_delay() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        var clock = Stopwatch.StartNew();

        // The random delay of each player is fixed, so the order of their first attempts is known.
        var players = new[] { 0.1, 0.5, 0.9 }
            .Select((random, index) => new Player($"spread{index}", random, clock))
            .ToArray();

        foreach (var player in players) player.Client.Connect("127.0.0.1", proxy.Port, player.Name, player.Name + "-key", []);
        Assert.True(Pump(() => players.All(player => player.Client.State == ConnectionState.Connected)),
            "The players never entered: " + Describe(players));

        foreach (var player in players) proxy.Silence(player.LocalEndPoint);
        Assert.True(Pump(() => players.All(player => player.FirstAttempt != null)),
            "Not every player started to take its session back: " + Describe(players));
        Assert.True(Pump(() => players.All(player => player.Client.State == ConnectionState.Connected)),
            "Not every player came back: " + Describe(players));

        _output.WriteLine(Describe(players));
        var attempts = players.Select(player => player.FirstAttempt.Value).ToArray();
        Assert.True(attempts[0] < attempts[1] && attempts[1] < attempts[2],
            "The first attempts did not follow the random delays: " + Describe(players));
        Assert.True(attempts[2] - attempts[0] >= TimeSpan.FromSeconds(0.25),
            "The first attempts were not spread apart: " + Describe(players));
        Assert.All(players, player => Assert.Contains(ConnectionState.Resuming, player.States));

        foreach (var player in players) player.Client.Disconnect();
        Pump(() => players.All(player => player.Client.State == ConnectionState.Disconnected));
    }

    /// <summary>
    /// Drains the main thread mailbox until the condition holds or the wait ends.
    /// </summary>
    private static bool Pump(Func<bool> condition) {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Wait) {
            MainThreadMailbox.Shared.Drain();
            if (condition()) return true;
            System.Threading.Thread.Sleep(10);
        }

        return false;
    }

    private static string Reason(Player player) {
        var failure = (ConnectionFailedResult) Harness.Hkmp.Property(Harness.Hkmp.Field(player.Client, "_connection"), "Failure");
        return failure == null ? "no failure" : failure is ConnectionFailedMessageResult message ? $"{failure.Reason}: {message.Message}" : failure.Reason.ToString();
    }

    private static string Describe(IEnumerable<Player> players) =>
        string.Join("; ", players.Select(player =>
            $"{player.Name}: {player.Client.State} ({Reason(player)}), first attempt {player.FirstAttempt?.TotalSeconds:0.000} s, " +
            $"states {string.Join(">", player.States)}"));

    /// <summary>
    /// One player's network client and what it went through.
    /// </summary>
    private sealed class Player {
        public Player(string name, double random, Stopwatch clock) {
            Name = name;
            Client = new NetClient(new PacketManager(), SessionServer.ShortTimeouts, () => random);
            Client.StateChangedEvent += state => States.Add(state);
            Client.TransportReplacedEvent += () => FirstAttempt ??= clock.Elapsed;
        }

        public string Name { get; }
        public NetClient Client { get; }
        public List<ConnectionState> States { get; } = [];
        public TimeSpan? FirstAttempt { get; private set; }

        /// <summary>
        /// The local endpoint of the client's current socket, which the proxy knows it by.
        /// </summary>
        public IPEndPoint LocalEndPoint {
            get {
                var dtls = Harness.Hkmp.Field(Client, "_dtlsClient");
                return (IPEndPoint) ((Socket) Harness.Hkmp.Field(dtls, "_socket")).LocalEndPoint;
            }
        }
    }
}
