using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Game.Server.Sessions;
using Hkmp.Networking;
using Hkmp.Networking.Timing;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// The session on its own: its automaton, its registry, the player ids and the admission policy.
/// </summary>
public class SessionTests {
    public static IEnumerable<object[]> ListedTransitions() {
        object[] Row(SessionState state, SessionEvent sessionEvent, SessionState next, SessionAction action) =>
            [state, sessionEvent, next, action];

        yield return Row(SessionState.Admitting, SessionEvent.Packet, SessionState.Admitting, SessionAction.DeliverToConnectionPhase);
        yield return Row(SessionState.Admitting, SessionEvent.Accepted, SessionState.Active, SessionAction.Admit);
        yield return Row(SessionState.Admitting, SessionEvent.Refused, SessionState.Refusing, SessionAction.SendRefusal);
        yield return Row(SessionState.Admitting, SessionEvent.AdmissionDeadline, SessionState.Ended, SessionAction.CloseConnection);
        yield return Row(SessionState.Admitting, SessionEvent.Leave, SessionState.Ended, SessionAction.CloseConnection);
        yield return Row(SessionState.Refusing, SessionEvent.ResponseDelivered, SessionState.Ended, SessionAction.CloseRefused);
        yield return Row(SessionState.Refusing, SessionEvent.AdmissionDeadline, SessionState.Ended, SessionAction.CloseRefused);
        yield return Row(SessionState.Refusing, SessionEvent.Leave, SessionState.Ended, SessionAction.CloseRefused);
        yield return Row(SessionState.Active, SessionEvent.Packet, SessionState.Active, SessionAction.Deliver);
        yield return Row(SessionState.Active, SessionEvent.Silence, SessionState.Suspended, SessionAction.Suspend);
        yield return Row(SessionState.Active, SessionEvent.Resumption, SessionState.Active, SessionAction.SwapConnection);
        yield return Row(SessionState.Active, SessionEvent.Leave, SessionState.Ended, SessionAction.AnnounceLeave);
        yield return Row(SessionState.Suspended, SessionEvent.Packet, SessionState.Active, SessionAction.ResumeFromPacket);
        yield return Row(SessionState.Suspended, SessionEvent.Resumption, SessionState.Active, SessionAction.SwapConnectionAndResume);
        yield return Row(SessionState.Suspended, SessionEvent.DropDeadline, SessionState.Ended, SessionAction.AnnounceLeave);
        yield return Row(SessionState.Suspended, SessionEvent.Replaced, SessionState.Ended, SessionAction.AnnounceLeave);
        yield return Row(SessionState.Suspended, SessionEvent.Leave, SessionState.Ended, SessionAction.AnnounceLeave);
    }

    [Theory]
    [MemberData(nameof(ListedTransitions))]
    public void Each_row_of_the_session_table_holds(object state, object sessionEvent, object next, object action) {
        Assert.Equal(new SessionTransition((SessionState) next, (SessionAction) action),
            SessionAutomaton.Next((SessionState) state, (SessionEvent) sessionEvent));
    }

    [Fact]
    public void Every_unlisted_event_keeps_the_state_and_does_nothing() {
        var listed = new HashSet<(SessionState, SessionEvent)>();
        foreach (var row in ListedTransitions()) listed.Add(((SessionState) row[0], (SessionEvent) row[1]));

        foreach (SessionState state in Enum.GetValues(typeof(SessionState))) {
            foreach (SessionEvent sessionEvent in Enum.GetValues(typeof(SessionEvent))) {
                if (listed.Contains((state, sessionEvent))) continue;
                Assert.Equal(new SessionTransition(state, SessionAction.None), SessionAutomaton.Next(state, sessionEvent));
            }
        }
    }

    [Fact]
    public void An_admitted_session_is_found_by_its_token_only() {
        var registry = NewRegistry(out _);
        var session = Open(registry, "a");
        registry.Admit(session, "key-a");

        Assert.Same(session, registry.Find(session.Token));
        Assert.Null(registry.Find(SessionToken.Create()));
        Assert.Null(registry.Find(SessionToken.None));
        Assert.True(registry.HasLiveSession("key-a"));
    }

    [Fact]
    public void A_resumption_keeps_the_player_id_moves_the_connection_and_renews_the_token() {
        var registry = NewRegistry(out _);
        var session = Open(registry, "old");
        registry.Admit(session, "key");
        var oldToken = session.Token;
        var arriving = Open(registry, "new");

        var resumption = registry.Resume(session, arriving);

        Assert.Equal(SessionAction.SwapConnection, resumption.Action);
        Assert.Equal("old", resumption.Previous.Name);
        Assert.Equal("new", session.Connection.Name);
        Assert.Same(session, registry.Of(session.Connection));
        Assert.Null(registry.Of(resumption.Previous));
        Assert.Null(registry.Find(oldToken));
        Assert.Same(session, registry.Find(session.Token));
        Assert.Equal(SessionState.Ended, arriving.State);
    }

    [Fact]
    public void A_suspended_session_comes_back_active_on_a_resumption() {
        var registry = NewRegistry(out _);
        var session = Open(registry, "old");
        registry.Admit(session, "key");
        registry.Handle(session, SessionEvent.Silence);

        var resumption = registry.Resume(session, Open(registry, "new"));

        Assert.Equal(SessionAction.SwapConnectionAndResume, resumption.Action);
        Assert.Equal(SessionState.Active, session.State);
    }

    [Fact]
    public void Only_a_suspended_session_gives_way_to_the_same_player_entering_again() {
        var registry = NewRegistry(out _);
        var session = Open(registry, "old");
        registry.Admit(session, "key");

        Assert.Same(session, registry.OfIdentity("key"));
        Assert.Equal(SessionAction.None, registry.Handle(session, SessionEvent.Replaced));
        Assert.Equal(SessionState.Active, session.State);

        registry.Handle(session, SessionEvent.Silence);
        Assert.Equal(SessionAction.AnnounceLeave, registry.Handle(session, SessionEvent.Replaced));
        Assert.Null(registry.OfIdentity("key"));
        Assert.False(registry.HasLiveSession("key"));
    }

    [Fact]
    public void An_ended_session_cannot_be_resumed_and_its_player_may_enter_again() {
        var registry = NewRegistry(out _);
        var session = Open(registry, "old");
        registry.Admit(session, "key");
        var token = session.Token;
        registry.Handle(session, SessionEvent.Silence);
        registry.Handle(session, SessionEvent.DropDeadline);

        Assert.Null(registry.Find(token));
        Assert.False(registry.HasLiveSession("key"));
    }

    [Fact]
    public void An_id_rests_after_its_session_ends_before_anyone_gets_it_again() {
        var now = TimeSpan.Zero;
        var ids = new PlayerIdPool(() => now, ConnectionTimeouts.Default.IdRest);
        var first = ids.Take();
        ids.Release(first);

        // Every other id goes into use; the only free one is resting.
        for (var i = 0; i < ushort.MaxValue; i++) {
            Assert.NotEqual(first, ids.Take());
        }

        Assert.Throws<InvalidOperationException>(() => ids.Take());

        now = ConnectionTimeouts.Default.IdRest;
        Assert.Equal(first, ids.Take());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_entries_silences_resumptions_and_departures_keep_one_session_per_player(int seed) {
        var random = new Random(seed);
        var registry = NewRegistry(out _);
        var identities = new[] { "k0", "k1", "k2", "k3" };
        var idOfIdentity = new Dictionary<string, ushort>();
        var connection = 0;

        for (var step = 0; step < 2000; step++) {
            var identity = identities[random.Next(identities.Length)];
            var live = registry.Players.FirstOrDefault(session => session.Identity == identity);
            switch (random.Next(5)) {
                case 0 when live == null:
                    var entering = Open(registry, $"c{connection++}");
                    registry.Admit(entering, identity);
                    idOfIdentity[identity] = entering.PlayerId;
                    break;
                case 1 when live != null:
                    registry.Handle(live, SessionEvent.Silence);
                    break;
                case 2 when live != null:
                    var arriving = Open(registry, $"c{connection++}");
                    registry.Resume(registry.Find(live.Token), arriving);
                    // S4: the id does not change on a resumption.
                    Assert.Equal(idOfIdentity[identity], live.PlayerId);
                    break;
                case 3 when live != null:
                    registry.Handle(live, random.Next(2) == 0 ? SessionEvent.Leave : SessionEvent.DropDeadline);
                    break;
                case 4 when live != null:
                    registry.Handle(live, SessionEvent.Packet);
                    break;
            }

            // S3: at most one live session per identity, and each with its own connection.
            var players = registry.Players.ToList();
            Assert.Equal(players.Count, players.Select(session => session.Identity).Distinct().Count());
            Assert.Equal(players.Count, players.Select(session => session.Connection).Distinct().Count());
            Assert.All(players, session => Assert.Same(session, registry.Of(session.Connection)));
            Assert.All(players, session => Assert.True(session.State is SessionState.Active or SessionState.Suspended));
        }
    }

    [Fact]
    public void The_policy_refuses_each_rule_with_its_reason() {
        var rules = new FakeRules();
        var policy = new AdmissionPolicy(rules);
        AdmissionRequest Request(string name = "player", ushort version = NetworkProtocol.Version) =>
            new(version, "key", name, "10.0.0.1");

        Assert.True(policy.DecideEntry(Request()).Accepted);

        Assert.Equal(AdmissionPolicy.IncompatibleVersionReason(1), policy.DecideEntry(Request(version: 1)).Reason);
        Assert.Equal(AdmissionPolicy.InvalidUsernameReason, policy.DecideEntry(Request("bad name")).Reason);
        Assert.Equal(AdmissionPolicy.InvalidUsernameReason, policy.DecideEntry(Request(new string('a', 21))).Reason);

        rules.NameInUse = true;
        Assert.Equal(ConnectionRejection.UsernameInUse, policy.DecideEntry(Request()).Reason);
        rules.NameInUse = false;

        rules.LiveSession = true;
        Assert.Equal(ConnectionRejection.UsernameInUse, policy.DecideEntry(Request()).Reason);
        rules.LiveSession = false;

        rules.Whitelist = true;
        Assert.Equal(AdmissionPolicy.NotWhitelistedReason, policy.DecideEntry(Request()).Reason);
        rules.PreListed = true;
        Assert.True(policy.DecideEntry(Request()).PromoteFromPreList);
        rules.Whitelist = false;

        rules.KeyBanned = true;
        Assert.Equal(AdmissionPolicy.BannedReason, policy.DecideEntry(Request()).Reason);
        rules.KeyBanned = false;

        rules.AddressBanned = true;
        Assert.Equal(AdmissionPolicy.BannedReason, policy.DecideEntry(Request()).Reason);
    }

    [Fact]
    public void A_resumption_counts_only_the_version_and_the_bans() {
        var rules = new FakeRules { NameInUse = true, LiveSession = true, Whitelist = true };
        var policy = new AdmissionPolicy(rules);
        var request = new AdmissionRequest(NetworkProtocol.Version, "key", "player", "10.0.0.1");

        Assert.True(policy.DecideResumption(request).Accepted);

        rules.AddressBanned = true;
        Assert.Equal(AdmissionPolicy.BannedReason, policy.DecideResumption(request).Reason);
    }

    [Fact]
    public void The_deadlines_keep_their_relations() {
        Assert.True(ConnectionTimeouts.Default.KeepsItsRelations);
        Assert.True(Harness.SessionServer.ShortTimeouts.KeepsItsRelations);
    }

    private static SessionRegistry<FakeConnection> NewRegistry(out PlayerIdPool ids) {
        ids = new PlayerIdPool(() => TimeSpan.Zero, ConnectionTimeouts.Default.IdRest);
        return new SessionRegistry<FakeConnection>(ids);
    }

    private static Session<FakeConnection> Open(SessionRegistry<FakeConnection> registry, string name) {
        return registry.Open(registry.TakeId(), new FakeConnection(name));
    }

    internal sealed class FakeConnection {
        public FakeConnection(string name) {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class FakeRules : IAdmissionRules {
        public bool KeyBanned;
        public bool AddressBanned;
        public bool Whitelist;
        public bool PreListed;
        public bool NameInUse;
        public bool LiveSession;

        public bool IsKeyBanned(string identity) => KeyBanned;
        public bool IsAddressBanned(string address) => AddressBanned;
        public bool WhitelistEnabled => Whitelist;
        public bool IsWhitelisted(string identity) => false;
        public bool IsPreListed(string username) => PreListed;
        public bool IsNameInUse(string username) => NameInUse;
        public bool HasLiveSession(string identity) => LiveSession;
    }
}
