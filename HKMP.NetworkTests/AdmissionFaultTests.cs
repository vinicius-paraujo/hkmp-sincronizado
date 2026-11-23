using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.NetworkTests.Harness;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hkmp.NetworkTests;

/// <summary>
/// Faults around the moment a player joins. The invariant behind every case: if one player can join, any other
/// player can join too, whatever happened to earlier connection attempts.
/// </summary>
public class AdmissionFaultTests {
    private static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Game data waits for the registration, which follows the acknowledgement of the acceptance.
    /// </summary>
    private static readonly TimeSpan SessionDataTimeout = TimeSpan.FromSeconds(8);

    private readonly ITestOutputHelper _output;

    public AdmissionFaultTests(ITestOutputHelper output) {
        _output = output;
    }

    [Theory]
    [InlineData(2, 5, 0)]
    [InlineData(4, 2, 0)]
    [InlineData(2, 5, 250)]
    public void Players_joining_at_the_same_time_all_join(int players, int rounds, int millisecondsApart) {
        var failures = 0;
        var aloneAfterwards = 0;
        for (var round = 1; round <= rounds; round++) {
            using var server = new TestServer();
            var clients = Enumerable.Range(1, players).Select(i => new TestClient($"round{round}-p{i}")).ToList();
            try {
                using var start = new Barrier(players);
                var threads = clients.Select((client, index) => new Thread(() => {
                    start.SignalAndWait();
                    Thread.Sleep(index * millisecondsApart);
                    client.Connect(server.Port);
                })).ToList();
                threads.ForEach(thread => thread.Start());
                threads.ForEach(thread => thread.Join());

                foreach (var client in clients) {
                    var joined = client.Error == null && DeliversAsItself(server, client);
                    if (!joined) failures++;
                    _output.WriteLine(client.Describe());
                }

                // A player arriving alone afterwards shows whether the failure outlived the collision.
                using var alone = new TestClient($"round{round}-alone");
                var aloneJoined = alone.Connect(server.Port) && DeliversAsItself(server, alone);
                if (aloneJoined) aloneAfterwards++;
                _output.WriteLine($"Round {round}: acceptor {server.AcceptorState}; {alone.Describe()}");
            } finally {
                clients.ForEach(client => client.Dispose());
            }
        }

        Assert.True(failures == 0,
            $"{failures} of {players * rounds} players could not join; a player alone afterwards joined in {aloneAfterwards} of {rounds} rounds.");
    }

    /// <summary>
    /// Players on the internet, 75 ms away from the host in each direction, joining a moment apart. Each handshake
    /// takes several round trips, so arrivals this close overlap, as after an outage when everyone reconnects.
    /// </summary>
    [Theory]
    [InlineData(250)]
    [InlineData(1000)]
    public void Players_over_the_internet_joining_a_moment_apart_all_join(int millisecondsApart) {
        const int rounds = 3;
        var failures = 0;
        for (var round = 1; round <= rounds; round++) {
            using var server = new TestServer();
            using var proxy = new FaultProxy(server.Port) { DelayMilliseconds = 75 };
            var clients = new[] { new TestClient($"round{round}-p1"), new TestClient($"round{round}-p2") };
            try {
                var threads = clients.Select((client, index) => new Thread(() => {
                    Thread.Sleep(index * millisecondsApart);
                    client.Connect(proxy.Port);
                })).ToList();
                threads.ForEach(thread => thread.Start());
                threads.ForEach(thread => thread.Join());

                foreach (var client in clients) {
                    var joined = client.Error == null && Delivers(server, client);
                    if (!joined) failures++;
                    _output.WriteLine($"{client.Describe()}; data received: {joined}");
                }

                _output.WriteLine($"Round {round}: acceptor {server.AcceptorState}");
            } finally {
                foreach (var client in clients) client.Dispose();
            }
        }

        Assert.True(failures == 0, $"{failures} of {rounds * 2} players could not join.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_player_vanishing_during_the_handshake_does_not_block_the_next(int datagramsBeforeVanishing) {
        using var server = new TestServer();
        using var proxy = new FaultProxy(server.Port) { CutClientsAfter = datagramsBeforeVanishing };
        using var vanishing = new TestClient("vanishing");
        var vanishingThread = new Thread(() => vanishing.Connect(proxy.Port));
        vanishingThread.Start();
        Thread.Sleep(500);

        using var next = new TestClient("next");
        var nextJoined = next.Connect(server.Port) && DeliversAsItself(server, next);
        _output.WriteLine($"{next.Describe()}; data attributed correctly: {nextJoined}");

        vanishingThread.Join();
        _output.WriteLine(vanishing.Describe());
        _output.WriteLine($"Acceptor after the vanished player gave up: {server.AcceptorState}");

        using var later = new TestClient("after the vanished one gave up");
        var laterJoined = later.Connect(server.Port) && DeliversAsItself(server, later);
        _output.WriteLine($"{later.Describe()}; data attributed correctly: {laterJoined}");

        Assert.True(nextJoined && laterJoined, "A half-done handshake kept other players out.");
    }

    [Fact]
    public void A_refused_handshake_does_not_stop_the_server_accepting() {
        using var server = new TestServer();
        var refusal = IncompatibleClient.Handshake(server.Port);
        _output.WriteLine($"Incompatible client: {(refusal == null ? "accepted" : refusal.GetType().Name + ": " + refusal.Message)}");
        Thread.Sleep(500);
        _output.WriteLine($"Acceptor after the refusal: {server.AcceptorState}");

        using var next = new TestClient("next");
        var joined = next.Connect(server.Port) && DeliversAsItself(server, next);
        _output.WriteLine($"{next.Describe()}; data attributed correctly: {joined}");

        Assert.True(joined, "After one refused handshake, a valid player could not join.");
    }

    [Fact]
    public void A_player_leaving_without_notice_does_not_disturb_the_others() {
        using var server = new TestServer();
        using var staying = new TestClient("staying");
        using var leaving = new TestClient("leaving");
        Assert.True(staying.Connect(server.Port), staying.Describe());
        Assert.True(leaving.Connect(server.Port), leaving.Describe());

        // The server keeps sending to everyone, as its update timers do until a client times out.
        using var updates = new Updates(server);
        leaving.Dispose();
        Thread.Sleep(1000);

        using var next = new TestClient("next");
        var nextJoined = next.Connect(server.Port) && DeliversAsItself(server, next);
        _output.WriteLine($"{next.Describe()}; data attributed correctly: {nextJoined}");

        var stayingHeard = DeliversAsItself(server, staying);
        _output.WriteLine($"Staying player still heard: {stayingHeard}; send errors on the server: {updates.Errors}");

        Assert.True(nextJoined && stayingHeard, "A player leaving without notice disturbed the server.");
    }

    [Fact]
    public void An_oversized_datagram_does_not_disturb_accepting() {
        using var server = new TestServer();
        using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            raw.SendTo(new byte[2000], new IPEndPoint(IPAddress.Loopback, server.Port));
        }

        Thread.Sleep(200);
        using var next = new TestClient("next");
        var joined = next.Connect(server.Port) && DeliversAsItself(server, next);
        _output.WriteLine($"{next.Describe()}; data attributed correctly: {joined}");

        Assert.True(joined, "A datagram above the receive buffer kept a valid player out.");
    }

    [Theory]
    [InlineData(1392)]
    [InlineData(1252)]
    public void The_handshake_fits_paths_with_a_smaller_mtu(int maxDatagram) {
        using var server = new TestServer();
        using (var measuring = new FaultProxy(server.Port))
        using (var probe = new TestClient("unlimited path")) {
            probe.Connect(measuring.Port);
            _output.WriteLine($"{probe.Describe()}; largest datagram: {measuring.Largest} bytes of UDP payload");
        }

        using var proxy = new FaultProxy(server.Port) { MaxDatagram = maxDatagram };
        using var client = new TestClient($"path limited to {maxDatagram} bytes");
        var joined = client.Connect(proxy.Port);
        _output.WriteLine($"{client.Describe()}; dropped datagrams: {proxy.Dropped}");

        Assert.True(joined, $"The handshake does not fit a path of {maxDatagram + 28} bytes per IP packet.");
    }

    [Fact]
    public void A_session_survives_a_source_port_change() {
        using var server = new SessionServer();
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("rebound", "rebound-key");
        var entry = client.Connect(proxy.Port);
        Assert.True(entry?.ConnectionResult == Networking.ServerConnectionResult.Accepted, "The player could not enter.");
        var firstToken = client.Token;

        client.Say("before");
        var before = server.WaitFor("before", SessionDataTimeout);
        Assert.True(before != null, "Data sent before the source port changed never reached the session.");

        // The NAT moves the game to another source port; the old connection stops reaching the server, and the
        // game connects again with the token of its session.
        proxy.Rebind(client.LocalEndPoint);
        var resumption = client.Connect(proxy.Port);
        client.Say("after");
        var after = server.WaitFor("after", SessionDataTimeout);
        _output.WriteLine($"Before the change: player {before}; resumed: {resumption?.Resumed}; after the change: player {after?.ToString() ?? "none"}");

        Assert.True(resumption?.Resumed == true, "The new connection did not take the session back.");
        Assert.True(after == before, "Data sent after the source port changed did not reach the same session.");
        Assert.False(firstToken.SequenceEqual(client.Token), "The resumption did not renew the session token.");
        Assert.Equal(new[] { before.Value }, server.Resumed.ToArray());
        Assert.Empty(server.TimedOut);
    }

    [Fact]
    public void A_new_connection_on_a_reused_address_joins() {
        using var server = new SessionServer();
        using var proxy = new FaultProxy(server.Port);
        var first = new SessionClient("first", "first-key");
        Assert.True(first.Connect(proxy.Port)?.ConnectionResult == Networking.ServerConnectionResult.Accepted);
        first.Say("first");
        var firstPlayer = server.WaitFor("first", SessionDataTimeout);
        var firstEndPoint = first.LocalEndPoint;

        // The first game vanishes, and the NAT gives the next connection the same public address and port.
        proxy.Silence(firstEndPoint);
        proxy.ReuseForNextClient(firstEndPoint);
        first.Dispose();

        using var next = new SessionClient("next", "next-key");
        var answer = next.Connect(proxy.Port);
        next.Say("next");
        var nextPlayer = server.WaitFor("next", SessionDataTimeout);
        _output.WriteLine($"First player {firstPlayer}; next answer: {answer?.ConnectionResult.ToString() ?? "none"}, resumed: {answer?.Resumed}; next player {nextPlayer?.ToString() ?? "none"}");

        Assert.True(nextPlayer != null, "A connection arriving on the address of a vanished session could not join.");
        Assert.False(answer.Resumed, "A connection without a token took over the session that used its address.");
        Assert.NotEqual(firstPlayer, nextPlayer);
    }

    [Fact]
    public void A_restarted_server_accepts_again() {
        using var server = new TestServer();
        var failures = 0;
        for (var restart = 1; restart <= 5; restart++) {
            server.Stop();
            server.Start();
            using var client = new TestClient($"after restart {restart}");
            var joined = client.Connect(server.Port) && DeliversAsItself(server, client);
            if (!joined) failures++;
            _output.WriteLine($"{client.Describe()}; data attributed correctly: {joined}");
        }

        Assert.True(failures == 0, $"{failures} of 5 connections after a restart failed.");
    }

    /// <summary>
    /// Whether text sent by the client reaches the server attributed to the client's own endpoint.
    /// </summary>
    private bool DeliversAsItself(TestServer server, TestClient client) {
        var text = $"{client.Name}-{Guid.NewGuid():N}";
        try {
            client.Send(text);
        } catch (Exception e) {
            _output.WriteLine($"  {client.Name} could not send: {e.GetType().Name}");
            return false;
        }

        var from = server.WaitFor(text, DataTimeout);
        var own = client.LocalEndPoint;
        if (from != null && from.Equals(own)) return true;

        _output.WriteLine($"  {client.Name} ({own}) sent data that the server received {Describe(from)}");
        return false;
    }

    /// <summary>
    /// Whether text sent by a client behind a relay reaches the server at all; the relay hides the client's port.
    /// </summary>
    private static bool Delivers(TestServer server, TestClient client) {
        var text = $"{client.Name}-{Guid.NewGuid():N}";
        try {
            client.Send(text);
        } catch (Exception) {
            return false;
        }

        return server.WaitFor(text, DataTimeout) != null;
    }

    private static string Describe(IPEndPoint endPoint) => endPoint == null ? "nothing" : $"from {endPoint}";

    /// <summary>
    /// Sends to every client every 20 ms in the background.
    /// </summary>
    private sealed class Updates : IDisposable {
        private volatile bool _stopped;
        private readonly Thread _thread;

        public int Errors { get; private set; }

        public Updates(TestServer server) {
            _thread = new Thread(() => {
                while (!_stopped) {
                    Errors += server.SendToAll("update");
                    Thread.Sleep(20);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose() {
            _stopped = true;
            _thread.Join();
        }
    }
}
