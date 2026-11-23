using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hkmp.Networking;
using Hkmp.NetworkTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Hkmp.NetworkTests;

/// <summary>
/// Faults a player meets after entering, whatever path connects it to the server. The server only sees datagrams
/// that arrive, arrive late or twice, or stop arriving, and each case below is one of those.
/// </summary>
public class SessionFaultTests {
    private static readonly TimeSpan SessionDataTimeout = TimeSpan.FromSeconds(8);

    private const int MessageSpacingMilliseconds = 60;

    private readonly ITestOutputHelper _output;

    public SessionFaultTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public void Lost_doubled_and_reordered_datagrams_still_deliver_every_message_once() {
        using var server = new SessionServer();
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("lossy", "lossy-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("ready");
        Assert.True(server.WaitFor("ready", SessionDataTimeout) != null, "The player never got its data through.");

        proxy.LoseEvery = 4;
        proxy.DuplicateEvery = 5;
        proxy.ReorderEvery = 3;
        var messages = Enumerable.Range(0, 20).Select(index => $"message {index}").ToArray();
        // A chat message waits in the outgoing packet until it is sent, and a second one would take its place, so the
        // messages are several send intervals apart.
        foreach (var message in messages) {
            client.Say(message);
            System.Threading.Thread.Sleep(MessageSpacingMilliseconds);
        }

        foreach (var message in messages) {
            Assert.True(server.WaitFor(message, SessionDataTimeout) != null, $"\"{message}\" never arrived.");
        }

        // Late duplicates and resends have time to arrive before the count.
        System.Threading.Thread.Sleep(500);

        _output.WriteLine($"Datagrams lost on the way: {proxy.Lost}");
        Assert.True(proxy.Lost > 0, "The path lost nothing, so the case tested no loss.");
        Assert.All(messages, message => Assert.Equal(1, server.CountOf(message)));
        Assert.Empty(server.TimedOut);
    }

    [Fact]
    public void Messages_lost_in_the_same_outage_all_arrive_once_the_network_is_back() {
        using var server = new SessionServer();
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("outage", "outage-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("ready");
        Assert.True(server.WaitFor("ready", SessionDataTimeout) != null, "The player never got its data through.");

        // Both messages leave while the network is out, so both arrive only as resends once it is back.
        proxy.Silence(client.LocalEndPoint);
        client.Say("first lost");
        System.Threading.Thread.Sleep(MessageSpacingMilliseconds);
        client.Say("second lost");
        System.Threading.Thread.Sleep(MessageSpacingMilliseconds);
        proxy.Restore(client.LocalEndPoint);

        var first = server.WaitFor("first lost", SessionDataTimeout);
        var second = server.WaitFor("second lost", SessionDataTimeout);
        _output.WriteLine($"First: {first?.ToString() ?? "none"}; second: {second?.ToString() ?? "none"}");

        Assert.True(first != null, "The first message lost in the outage never arrived.");
        Assert.True(second != null, "The second message lost in the outage never arrived.");
        Assert.Equal(1, server.CountOf("first lost"));
        Assert.Equal(1, server.CountOf("second lost"));
    }

    [Fact]
    public void A_silence_shorter_than_the_drop_deadline_keeps_the_session() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("paused", "paused-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("before");
        var player = server.WaitFor("before", SessionDataTimeout);
        Assert.True(player != null, "The player never got its data through.");

        // The network goes away long enough for the server to notice, and comes back before the session ends.
        proxy.Silence(client.LocalEndPoint);
        var silenced = SessionServer.WaitUntilListed(server.Silenced, player.Value, server.Timeouts.Drop);
        proxy.Restore(client.LocalEndPoint);
        client.Say("after");
        var after = server.WaitFor("after", SessionDataTimeout);
        _output.WriteLine($"Player {player}; silenced: {silenced}; after the silence: {after?.ToString() ?? "none"}");

        Assert.True(silenced, "The server did not notice the silence.");
        Assert.Equal(player, after);
        Assert.Empty(server.TimedOut);
    }

    [Fact]
    public void A_silence_longer_than_the_drop_deadline_ends_the_session_and_the_player_enters_anew() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("gone", "gone-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("before");
        var player = server.WaitFor("before", SessionDataTimeout);
        Assert.True(player != null, "The player never got its data through.");

        proxy.Silence(client.LocalEndPoint);
        var ended = SessionServer.WaitUntilListed(server.TimedOut, player.Value, server.Timeouts.Drop + server.Timeouts.Drop);

        // The game comes back with the token of a session that no longer exists.
        var entry = client.Connect(proxy.Port);
        client.Say("again");
        var again = server.WaitFor("again", SessionDataTimeout);
        _output.WriteLine($"Player {player}; ended: {ended}; answer: {entry?.ConnectionResult.ToString() ?? "none"}, resumed: {entry?.Resumed}; new player {again?.ToString() ?? "none"}");

        Assert.True(ended, "The session outlived the drop deadline.");
        Assert.True(entry?.ConnectionResult == ServerConnectionResult.Accepted, "The player could not enter again.");
        Assert.False(entry.Resumed, "An ended session was taken back.");
        Assert.True(again != null, "The new session never got its data through.");
        Assert.NotEqual(player, again);
    }

    [Fact]
    public void Reliable_data_around_a_resumption_reaches_the_server_exactly_once() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("steady", "steady-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("ready");
        var player = server.WaitFor("ready", SessionDataTimeout);
        Assert.True(player != null, "The player never got its data through.");

        // Some messages go out just before the silence, so their confirmations may be lost; the rest go out into
        // it and reach nobody. Each message gets a packet of its own.
        var messages = new List<string>();
        for (var i = 0; i < 5; i++) messages.Add(SayAndWait(client, $"before-{i}"));
        proxy.Silence(client.LocalEndPoint);
        for (var i = 0; i < 5; i++) messages.Add(SayAndWait(client, $"during-{i}"));
        var silenced = SessionServer.WaitUntilListed(server.Silenced, player.Value, server.Timeouts.Drop);

        var answer = client.Resume(proxy.Port);
        messages.Add(SayAndWait(client, "after"));
        var arrived = messages.All(message => server.WaitFor(message, SessionDataTimeout) != null);
        Thread.Sleep(500);
        var counts = messages.Select(message => $"{message}: {server.CountOf(message)}").ToList();
        _output.WriteLine($"Silenced: {silenced}; answer: {answer?.ConnectionResult.ToString() ?? "none"}, resumed: {answer?.Resumed}; {string.Join(", ", counts)}");

        Assert.True(silenced, "The server did not notice the silence.");
        Assert.True(answer?.Resumed == true, "The session was not taken back.");
        Assert.True(arrived, "Reliable data sent around the resumption was lost.");
        Assert.All(messages, message => Assert.Equal(1, server.CountOf(message)));
    }

    [Fact]
    public void Reliable_data_the_server_sends_during_a_silence_reaches_the_client_exactly_once() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        using var client = new SessionClient("listener", "listener-key");
        Assert.True(client.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        client.Say("ready");
        var player = server.WaitFor("ready", SessionDataTimeout);
        Assert.True(player != null, "The player never got its data through.");

        var messages = new List<string>();
        for (var i = 0; i < 5; i++) messages.Add($"before-{i}");
        foreach (var message in messages) server.SendChat(player.Value, message);
        proxy.Silence(client.LocalEndPoint);
        for (var i = 0; i < 5; i++) {
            messages.Add($"during-{i}");
            server.SendChat(player.Value, $"during-{i}");
        }

        var silenced = SessionServer.WaitUntilListed(server.Silenced, player.Value, server.Timeouts.Drop);
        var answer = client.Resume(proxy.Port);
        var arrived = SpinWait.SpinUntil(() => messages.All(client.Heard.Contains), SessionDataTimeout);
        Thread.Sleep(500);
        var heard = client.Heard.ToList();
        _output.WriteLine($"Silenced: {silenced}; resumed: {answer?.Resumed}; heard: {string.Join(", ", heard)}");

        Assert.True(silenced, "The server did not notice the silence.");
        Assert.True(answer?.Resumed == true, "The session was not taken back.");
        Assert.True(arrived, "Reliable data the server sent around the resumption was lost.");
        Assert.All(messages, message => Assert.Equal(1, heard.Count(entry => entry == message)));
    }

    private static string SayAndWait(SessionClient client, string message) {
        client.Say(message);
        Thread.Sleep(60);
        return message;
    }

    [Fact]
    public void A_game_opened_again_without_the_token_takes_the_place_of_its_suspended_session() {
        using var server = new SessionServer(SessionServer.ShortTimeouts);
        using var proxy = new FaultProxy(server.Port);
        using var closed = new SessionClient("closed", "closed-key");
        Assert.True(closed.Connect(proxy.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        closed.Say("before");
        var player = server.WaitFor("before", SessionDataTimeout);
        Assert.True(player != null, "The player never got its data through.");

        // The game closes without a word, and the same player opens it again, with no token, before the drop.
        proxy.Silence(closed.LocalEndPoint);
        var silenced = SessionServer.WaitUntilListed(server.Silenced, player.Value, server.Timeouts.Drop);
        using var reopened = new SessionClient("closed", "closed-key");
        var answer = reopened.Connect(server.Port);
        reopened.Say("again");
        var again = server.WaitFor("again", SessionDataTimeout);
        _output.WriteLine($"Player {player}; silenced: {silenced}; answer: {answer?.ConnectionResult.ToString() ?? "none"} {answer?.ConnectionRejectedMessage}; new player {again?.ToString() ?? "none"}");

        Assert.True(silenced, "The server did not notice the silence.");
        Assert.True(answer?.ConnectionResult == ServerConnectionResult.Accepted, "The player was refused while its old session was suspended.");
        Assert.False(answer.Resumed, "A game without the token took the old session back.");
        Assert.Contains(player.Value, server.TimedOut);
        Assert.True(again != null, "The new session never got its data through.");
    }

    [Fact]
    public void A_second_game_with_a_name_in_use_is_refused_while_the_first_session_lives() {
        using var server = new SessionServer();
        using var first = new SessionClient("alpha", "alpha-key");
        Assert.True(first.Connect(server.Port)?.ConnectionResult == ServerConnectionResult.Accepted);
        first.Say("first");
        var player = server.WaitFor("first", SessionDataTimeout);
        Assert.True(player != null, "The first player never got its data through.");

        // Another player picks the same name, and the same player opens a second game without the session token.
        using var stranger = new SessionClient("alpha", "other-key");
        var strangerAnswer = stranger.Connect(server.Port);
        using var twin = new SessionClient("alpha", "alpha-key");
        var twinAnswer = twin.Connect(server.Port);
        first.Say("still");
        var still = server.WaitFor("still", SessionDataTimeout);
        _output.WriteLine($"Stranger: {strangerAnswer?.ConnectionRejectedMessage ?? "no answer"}; twin: {twinAnswer?.ConnectionRejectedMessage ?? "no answer"}; first player after them: {still?.ToString() ?? "none"}");

        Assert.Equal(ServerConnectionResult.RejectedOther, strangerAnswer?.ConnectionResult);
        Assert.Equal(ConnectionRejection.UsernameInUse, strangerAnswer.ConnectionRejectedMessage);
        Assert.Equal(ServerConnectionResult.RejectedOther, twinAnswer?.ConnectionResult);
        Assert.Equal(ConnectionRejection.UsernameInUse, twinAnswer.ConnectionRejectedMessage);
        Assert.Equal(player, still);
    }
}
