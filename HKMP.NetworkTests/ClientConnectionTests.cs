using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Api.Client;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Networking.Client.Connection;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Timing;
using Hkmp.Ui;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// The client's connection: its automaton, the connection over a fake transport with a clock the test moves, the
/// policy and deadlines of the attempts, the mailbox into the main thread, and the layout of the player list.
/// </summary>
public class ClientConnectionTests {
    private static readonly byte[] OldToken = [1, 2, 3];
    private static readonly byte[] NewToken = [4, 5, 6];

    public static IEnumerable<object[]> ListedTransitions() {
        object[] Row(ConnectionState state, ClientConnectionEvent connectionEvent, ConnectionState next, ClientConnectionAction action) =>
            [state, connectionEvent, next, action];

        yield return Row(ConnectionState.Disconnected, ClientConnectionEvent.Connect, ConnectionState.Connecting, ClientConnectionAction.StartAttempt);
        yield return Row(ConnectionState.Connecting, ClientConnectionEvent.HandshakeSucceeded, ConnectionState.Admitting, ClientConnectionAction.SendClientInfo);
        yield return Row(ConnectionState.Connecting, ClientConnectionEvent.HandshakeFailed, ConnectionState.Failed, ClientConnectionAction.RecordFailure);
        yield return Row(ConnectionState.Connecting, ClientConnectionEvent.Leave, ConnectionState.Disconnected, ClientConnectionAction.CancelAttempt);
        yield return Row(ConnectionState.Admitting, ClientConnectionEvent.Accepted, ConnectionState.Connected, ClientConnectionAction.Admit);
        yield return Row(ConnectionState.Admitting, ClientConnectionEvent.AcceptedAsNew, ConnectionState.Connected, ClientConnectionAction.Admit);
        yield return Row(ConnectionState.Admitting, ClientConnectionEvent.Refused, ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure);
        yield return Row(ConnectionState.Admitting, ClientConnectionEvent.AdmissionDeadline, ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure);
        yield return Row(ConnectionState.Admitting, ClientConnectionEvent.Leave, ConnectionState.Disconnected, ClientConnectionAction.Close);
        yield return Row(ConnectionState.Connected, ClientConnectionEvent.Silence, ConnectionState.Resuming, ClientConnectionAction.BeginResuming);
        yield return Row(ConnectionState.Connected, ClientConnectionEvent.Ended, ConnectionState.Failed, ClientConnectionAction.CloseAndRecordFailure);
        yield return Row(ConnectionState.Connected, ClientConnectionEvent.Leave, ConnectionState.Disconnected, ClientConnectionAction.SendLeaveAndClose);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.Packet, ConnectionState.Connected, ClientConnectionAction.EndResumingOnOldConnection);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.RetryDue, ConnectionState.Resuming, ClientConnectionAction.StartAttempt);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.HandshakeSucceeded, ConnectionState.Resuming, ClientConnectionAction.SendClientInfo);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.HandshakeFailed, ConnectionState.Resuming, ClientConnectionAction.ScheduleRetry);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.AdmissionDeadline, ConnectionState.Resuming, ClientConnectionAction.ScheduleRetry);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.Accepted, ConnectionState.Connected, ClientConnectionAction.Resume);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.AcceptedAsNew, ConnectionState.Connected, ClientConnectionAction.Reenter);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.Refused, ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.GiveUp, ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.Ended, ConnectionState.Failed, ClientConnectionAction.CancelAndRecordFailure);
        yield return Row(ConnectionState.Resuming, ClientConnectionEvent.Leave, ConnectionState.Disconnected, ClientConnectionAction.CancelAndClose);
        yield return Row(ConnectionState.Failed, ClientConnectionEvent.Leave, ConnectionState.Disconnected, ClientConnectionAction.Publish);
    }

    [Theory]
    [MemberData(nameof(ListedTransitions))]
    public void Each_row_of_the_client_table_holds(object state, object connectionEvent, object next, object action) {
        Assert.Equal(new ClientConnectionTransition((ConnectionState) next, (ClientConnectionAction) action),
            ClientConnectionAutomaton.Next((ConnectionState) state, (ClientConnectionEvent) connectionEvent));
    }

    [Fact]
    public void Every_unlisted_event_keeps_the_state_and_does_nothing() {
        var listed = new HashSet<(ConnectionState, ClientConnectionEvent)>();
        foreach (var row in ListedTransitions()) listed.Add(((ConnectionState) row[0], (ClientConnectionEvent) row[1]));

        foreach (ConnectionState state in Enum.GetValues(typeof(ConnectionState))) {
            foreach (ClientConnectionEvent connectionEvent in Enum.GetValues(typeof(ClientConnectionEvent))) {
                if (listed.Contains((state, connectionEvent))) continue;
                Assert.Equal(new ClientConnectionTransition(state, ClientConnectionAction.None),
                    ClientConnectionAutomaton.Next(state, connectionEvent));
            }
        }
    }

    [Fact]
    public void A_new_player_is_admitted_with_an_empty_token_and_keeps_the_one_it_is_given() {
        var bench = new Bench();
        bench.Connection.Connect(keepSession: false);
        Assert.Equal(ConnectionState.Connecting, bench.Connection.State);

        bench.Port.Succeed();
        Assert.Equal(ConnectionState.Admitting, bench.Connection.State);
        Assert.Empty(bench.Port.SentTokens[0]);

        bench.Connection.OnAnswer(Accepted(OldToken, resumed: false));
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
        Assert.Equal(OldToken, bench.Connection.SessionToken);
        Assert.Null(bench.Connection.Failure);
    }

    [Fact]
    public void An_admission_without_answer_fails_at_its_deadline_and_closes_the_connection() {
        var bench = new Bench();
        bench.Connection.Connect(keepSession: false);
        bench.Port.Succeed();

        bench.Advance(bench.Timeouts.Admission - TimeSpan.FromMilliseconds(1));
        Assert.Equal(ConnectionState.Admitting, bench.Connection.State);
        bench.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(ConnectionState.Failed, bench.Connection.State);
        Assert.Equal(ConnectionFailedReason.TimedOut, bench.Connection.Failure.Reason);
        Assert.Equal(1, bench.Port.Closes);
    }

    [Fact]
    public void A_refusal_fails_with_the_servers_message_and_a_new_connect_starts_over() {
        var bench = new Bench();
        bench.Connection.Connect(keepSession: false);
        bench.Port.Succeed();
        bench.Connection.OnAnswer(new ServerInfo {
            ConnectionResult = ServerConnectionResult.RejectedOther,
            ConnectionRejectedMessage = "Username already in use"
        });

        Assert.Equal(ConnectionState.Failed, bench.Connection.State);
        Assert.Equal("Username already in use", ((ConnectionFailedMessageResult) bench.Connection.Failure).Message);

        bench.Connection.Connect(keepSession: false);
        Assert.Equal(ConnectionState.Connecting, bench.Connection.State);
        Assert.Null(bench.Connection.Failure);
    }

    [Theory]
    [InlineData(typeof(SocketException), ConnectionFailedReason.SocketException)]
    [InlineData(typeof(System.IO.IOException), ConnectionFailedReason.IOException)]
    [InlineData(typeof(InvalidOperationException), ConnectionFailedReason.IOException)]
    public void Any_handshake_exception_is_a_failure_with_a_reason(Type exception, object reason) {
        var bench = new Bench();
        bench.Connection.Connect(keepSession: false);
        bench.Port.Fail((Exception) Activator.CreateInstance(exception));

        Assert.Equal(ConnectionState.Failed, bench.Connection.State);
        Assert.Equal((ConnectionFailedReason) reason, bench.Connection.Failure.Reason);
    }

    [Fact]
    public void Silence_waits_the_random_delay_before_the_first_attempt_and_presents_the_old_token() {
        var bench = Connected(random: 0.5);

        bench.Connection.OnSilence();
        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);
        Assert.Equal(1, bench.Port.Handshakes);

        var jitter = TimeSpan.FromTicks(bench.Timeouts.RetryJitter.Ticks / 2);
        bench.Advance(jitter - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, bench.Port.Handshakes);
        bench.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, bench.Port.Handshakes);
        Assert.Equal(1, bench.Connection.ResumeAttempts);

        bench.Port.Succeed();
        Assert.Equal(OldToken, bench.Port.SentTokens[1]);
    }

    [Fact]
    public void Failed_attempts_wait_longer_each_time_up_to_the_limit() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);
        Assert.Equal(2, bench.Port.Handshakes);

        var expected = new[] { 1.0, 1.5, 2.25, 3.375, 5.0, 5.0 };
        foreach (var seconds in expected) {
            var handshakes = bench.Port.Handshakes;
            bench.Port.Fail(new SocketException());
            bench.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
            Assert.Equal(handshakes, bench.Port.Handshakes);
            bench.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(handshakes + 1, bench.Port.Handshakes);
        }

        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);
    }

    [Fact]
    public void The_old_connection_answering_before_the_attempt_cancels_it() {
        var bench = Connected(random: 0.9);
        bench.Connection.OnSilence();

        bench.Connection.OnPacketAfterSilence();
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);

        bench.Advance(bench.Timeouts.GiveUp);
        Assert.Equal(1, bench.Port.Handshakes);
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
    }

    [Fact]
    public void A_packet_after_the_old_connection_was_replaced_waits_for_the_servers_answer() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);
        bench.Port.Succeed();

        bench.Connection.OnPacketAfterSilence();
        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);

        bench.Connection.OnAnswer(Accepted(NewToken, resumed: true));
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
    }

    [Theory]
    [InlineData(true, ClientConnectionAction.Resume)]
    [InlineData(false, ClientConnectionAction.Reenter)]
    public void The_servers_answer_says_whether_the_session_was_taken_back(bool resumed, object action) {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);
        bench.Port.Succeed();

        bench.Connection.OnAnswer(Accepted(NewToken, resumed));

        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
        Assert.Equal((ClientConnectionAction) action, bench.Transitions[bench.Transitions.Count - 1].Action);
        Assert.Equal(NewToken, bench.Connection.SessionToken);
        bench.Advance(bench.Timeouts.GiveUp);
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
    }

    [Fact]
    public void An_attempt_answered_too_late_is_retried() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);
        bench.Port.Succeed();

        bench.Advance(bench.Timeouts.Admission);
        bench.Advance(bench.Timeouts.RetryFirst);

        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);
        Assert.Equal(3, bench.Port.Handshakes);
    }

    [Fact]
    public void The_client_gives_up_at_the_deadline_and_forgets_the_token() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();

        bench.Advance(bench.Timeouts.GiveUp);

        Assert.Equal(ConnectionState.Failed, bench.Connection.State);
        Assert.Equal(ConnectionFailedReason.TimedOut, bench.Connection.Failure.Reason);
        Assert.Empty(bench.Connection.SessionToken);
        Assert.True(bench.Port.Closes >= 1);
    }

    [Fact]
    public void Leaving_while_resuming_cancels_and_a_late_result_moves_nothing() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);

        bench.Connection.Leave();
        Assert.Equal(ConnectionState.Disconnected, bench.Connection.State);

        bench.Port.Succeed();
        bench.Connection.OnAnswer(Accepted(NewToken, resumed: true));
        bench.Advance(bench.Timeouts.GiveUp);
        Assert.Equal(ConnectionState.Disconnected, bench.Connection.State);
    }

    [Fact]
    public void A_connected_player_who_leaves_tells_the_server() {
        var bench = Connected(random: 0);

        bench.Connection.Leave();

        Assert.Equal(ConnectionState.Disconnected, bench.Connection.State);
        Assert.Equal(1, bench.Port.Leaves);
        Assert.Empty(bench.Connection.SessionToken);
    }

    [Fact]
    public void The_policy_spreads_the_first_attempt_and_grows_the_later_ones() {
        var timeouts = ConnectionTimeouts.Default;
        Assert.Equal(TimeSpan.Zero, new ReconnectPolicy(timeouts, () => 0).NextDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(0.75), new ReconnectPolicy(timeouts, () => 0.5).NextDelay(0));
        Assert.True(new ReconnectPolicy(timeouts, () => 0.999).NextDelay(0) < timeouts.RetryJitter);

        var policy = new ReconnectPolicy(timeouts, () => 0);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(1.5), policy.NextDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.NextDelay(20));
    }

    [Fact]
    public void The_game_and_test_deadlines_keep_their_relations() {
        Assert.True(ConnectionTimeouts.Default.KeepsItsRelations);
        Assert.True(Harness.SessionServer.ShortTimeouts.KeepsItsRelations);
        Assert.Equal(TimeSpan.FromSeconds(10), ConnectionTimeouts.Default.ClientHandshake);
        Assert.Equal(TimeSpan.FromSeconds(120), ConnectionTimeouts.Default.GiveUp);
    }

    [Fact]
    public void Each_broken_relation_is_caught() {
        var good = ConnectionTimeouts.Default;
        Assert.True(Table().KeepsItsRelations);
        Assert.False(Table(serverHandshake: good.ClientHandshake).KeepsItsRelations);
        Assert.False(Table(retryJitter: good.Drop).KeepsItsRelations);
        Assert.False(Table(silence: good.Drop).KeepsItsRelations);
        Assert.False(Table(giveUp: good.Drop).KeepsItsRelations);
        Assert.False(Table(idRest: good.Drop).KeepsItsRelations);
    }

    /// <summary>
    /// The game's table with the given deadlines changed.
    /// </summary>
    private static ConnectionTimeouts Table(
        TimeSpan? silence = null,
        TimeSpan? idRest = null,
        TimeSpan? serverHandshake = null,
        TimeSpan? retryJitter = null,
        TimeSpan? giveUp = null
    ) {
        var good = ConnectionTimeouts.Default;
        return new ConnectionTimeouts(
            good.Admission,
            silence ?? good.Silence,
            good.Drop,
            idRest ?? good.IdRest,
            serverHandshake ?? good.ServerHandshake,
            good.ClientHandshake,
            retryJitter ?? good.RetryJitter,
            good.RetryFirst,
            good.RetryGrowth,
            good.RetryMax,
            giveUp ?? good.GiveUp
        );
    }

    [Fact]
    public void The_mailbox_runs_work_in_order_and_leaves_work_posted_meanwhile_for_the_next_drain() {
        var mailbox = new MainThreadMailbox();
        var ran = new List<int>();
        var drains = 0;
        mailbox.Drained += () => drains++;

        mailbox.Post(() => ran.Add(1));
        mailbox.Post(() => {
            ran.Add(2);
            mailbox.Post(() => ran.Add(3));
        });
        mailbox.Drain();
        Assert.Equal(new[] { 1, 2 }, ran);
        Assert.Equal(1, drains);

        mailbox.Drain();
        Assert.Equal(new[] { 1, 2, 3 }, ran);
    }

    [Fact]
    public void Failing_work_neither_loses_the_rest_of_the_drain_nor_the_deadlines() {
        var mailbox = new MainThreadMailbox();
        var ran = new List<int>();
        var ticks = 0;
        mailbox.Drained += () => throw new InvalidOperationException("a failing owner of deadlines");
        mailbox.Drained += () => ticks++;

        mailbox.Post(() => throw new InvalidOperationException("a failing listener"));
        mailbox.Post(() => ran.Add(1));
        mailbox.Drain();

        Assert.Equal(new[] { 1 }, ran);
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void A_handshake_result_after_a_failing_listener_still_moves_the_connection() {
        var bench = new Bench();
        bench.Connection.Transitioned += (_, _) => throw new InvalidOperationException("a failing listener");

        bench.Mailbox.Post(() => bench.Connection.Connect(keepSession: false));
        bench.Mailbox.Post(() => bench.Port.Succeed());
        bench.Mailbox.Drain();

        Assert.Equal(ConnectionState.Admitting, bench.Connection.State);
    }

    [Fact]
    public void An_answer_to_an_attempt_already_given_up_is_ignored() {
        var bench = Connected(random: 0);
        bench.Connection.OnSilence();
        bench.Advance(TimeSpan.Zero);
        bench.Port.Succeed();
        Assert.True(bench.Connection.AwaitingAnswer);

        bench.Advance(bench.Timeouts.Admission);
        Assert.False(bench.Connection.AwaitingAnswer);
        bench.Connection.OnAnswer(Accepted(NewToken, resumed: true));
        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);

        bench.Advance(bench.Timeouts.RetryFirst);
        Assert.False(bench.Connection.AwaitingAnswer);
        bench.Connection.OnAnswer(Accepted(NewToken, resumed: true));
        Assert.Equal(ConnectionState.Resuming, bench.Connection.State);

        bench.Port.Succeed();
        bench.Connection.OnAnswer(Accepted(NewToken, resumed: true));
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
    }

    [Fact]
    public void An_attempt_turns_an_exception_into_a_posted_failure() {
        var mailbox = new MainThreadMailbox();
        Exception failure = null;

        ConnectAttempt.Start(() => throw new SocketException(), () => { }, mailbox, () => { }, error => failure = error);

        Assert.True(SpinWait.SpinUntil(() => {
            mailbox.Drain();
            return failure != null;
        }, TimeSpan.FromSeconds(5)));
        Assert.IsType<SocketException>(failure);
    }

    [Fact]
    public void A_cancelled_attempt_posts_nothing_and_closes_what_it_opened() {
        var mailbox = new MainThreadMailbox();
        var release = new ManualResetEventSlim();
        var discarded = new ManualResetEventSlim();
        var delivered = false;

        var attempt = ConnectAttempt.Start(() => release.Wait(), discarded.Set, mailbox, () => delivered = true, _ => delivered = true);
        attempt.Cancel();
        release.Set();

        Assert.True(discarded.Wait(TimeSpan.FromSeconds(5)));
        mailbox.Drain();
        Assert.False(delivered);
    }

    [Theory]
    [InlineData(0, 10, 0, 0)]
    [InlineData(3, 10, 3, 0)]
    [InlineData(10, 10, 10, 0)]
    [InlineData(11, 10, 9, 2)]
    [InlineData(40, 10, 9, 31)]
    [InlineData(5, 0, 0, 0)]
    public void The_player_list_fits_the_rows_and_sums_up_the_rest(int players, int rows, int shown, int hidden) {
        Assert.Equal((shown, hidden), PlayerListLayout.Fit(players, rows));
    }

    [Fact]
    public void The_rows_follow_the_height() {
        Assert.Equal(28, PlayerListLayout.RowsIn(985f, 34f));
        Assert.Equal(0, PlayerListLayout.RowsIn(20f, 34f));
        Assert.Equal(0, PlayerListLayout.RowsIn(500f, 0f));
    }

    private static ServerInfo Accepted(byte[] token, bool resumed) => new() {
        ConnectionResult = ServerConnectionResult.Accepted,
        SessionToken = token,
        Resumed = resumed
    };

    private static Bench Connected(double random) {
        var bench = new Bench(random);
        bench.Connection.Connect(keepSession: false);
        bench.Port.Succeed();
        bench.Connection.OnAnswer(Accepted(OldToken, resumed: false));
        Assert.Equal(ConnectionState.Connected, bench.Connection.State);
        return bench;
    }

    /// <summary>
    /// A connection over a fake transport, with a clock and a mailbox the test drives.
    /// </summary>
    private sealed class Bench {
        private TimeSpan _now;

        public Bench(double random = 0) {
            Mailbox = new MainThreadMailbox();
            Connection = new ClientConnection(Port, Timeouts, new ReconnectPolicy(Timeouts, () => random), () => _now, Mailbox);
            Connection.Transitioned += (_, transition) => Transitions.Add(transition);
        }

        public ConnectionTimeouts Timeouts { get; } = ConnectionTimeouts.Default;
        public FakePort Port { get; } = new();
        public MainThreadMailbox Mailbox { get; }
        public ClientConnection Connection { get; }
        public List<ClientConnectionTransition> Transitions { get; } = [];

        public void Advance(TimeSpan time) {
            _now += time;
            Mailbox.Drain();
        }
    }

    /// <summary>
    /// Records what the connection asks of the transport; the test decides how each handshake ends.
    /// </summary>
    private sealed class FakePort : IClientConnectionPort {
        private Action _succeeded;
        private Action<Exception> _failed;

        public int Handshakes { get; private set; }
        public int Closes { get; private set; }
        public int Leaves { get; private set; }
        public List<byte[]> SentTokens { get; } = [];

        public ConnectAttempt BeginHandshake(Action succeeded, Action<Exception> failed) {
            Handshakes++;
            _succeeded = succeeded;
            _failed = failed;
            return null;
        }

        public void Succeed() => _succeeded();

        public void Fail(Exception error) => _failed(error);

        public void SendClientInfo(byte[] sessionToken) => SentTokens.Add(sessionToken);

        public void SendLeave() => Leaves++;

        public void Close() => Closes++;
    }
}
