using System;
using System.Collections.Generic;
using Hkmp.Game.Client.Hosting;
using Hkmp.Networking.Server.Lifecycle;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// Hosting on its own: its automaton, and the flow over a fake server, local client and menus. The invariant behind
/// every case: the save loads only with the server open and the local client connected.
/// </summary>
public class HostingFlowTests {
    public static IEnumerable<object[]> ListedTransitions() {
        object[] Row(HostingState state, HostingEvent hostingEvent, HostingState next, HostingAction action) =>
            [state, hostingEvent, next, action];

        yield return Row(HostingState.Idle, HostingEvent.Host, HostingState.StartingServer, HostingAction.StartServer);
        yield return Row(HostingState.StartingServer, HostingEvent.ServerOpen, HostingState.ConnectingLocal, HostingAction.ConnectLocal);
        yield return Row(HostingState.StartingServer, HostingEvent.ServerFailed, HostingState.Undoing, HostingAction.StopServer);
        yield return Row(HostingState.StartingServer, HostingEvent.Leave, HostingState.Undoing, HostingAction.StopServer);
        yield return Row(HostingState.ConnectingLocal, HostingEvent.ClientConnected, HostingState.Loading, HostingAction.LoadSave);
        foreach (var state in new[] { HostingState.ConnectingLocal, HostingState.Loading, HostingState.Hosting }) {
            foreach (var hostingEvent in new[] { HostingEvent.ClientFailed, HostingEvent.ServerFailed, HostingEvent.Leave }) {
                yield return Row(state, hostingEvent, HostingState.Undoing, HostingAction.DisconnectAndStopServer);
            }
        }

        yield return Row(HostingState.Loading, HostingEvent.GameLoaded, HostingState.Hosting, HostingAction.MarkLoaded);
        yield return Row(HostingState.Undoing, HostingEvent.ServerStopped, HostingState.Idle, HostingAction.Finish);
    }

    [Theory]
    [MemberData(nameof(ListedTransitions))]
    public void Each_row_of_the_hosting_table_holds(object state, object hostingEvent, object next, object action) {
        Assert.Equal(new HostingTransition((HostingState) next, (HostingAction) action),
            HostingAutomaton.Next((HostingState) state, (HostingEvent) hostingEvent));
    }

    [Fact]
    public void Every_unlisted_event_keeps_the_state_and_does_nothing() {
        var listed = new HashSet<(HostingState, HostingEvent)>();
        foreach (var row in ListedTransitions()) listed.Add(((HostingState) row[0], (HostingEvent) row[1]));

        foreach (HostingState state in Enum.GetValues(typeof(HostingState))) {
            foreach (HostingEvent hostingEvent in Enum.GetValues(typeof(HostingEvent))) {
                if (listed.Contains((state, hostingEvent))) continue;
                Assert.Equal(new HostingTransition(state, HostingAction.None), HostingAutomaton.Next(state, hostingEvent));
            }
        }
    }

    [Fact]
    public void The_save_loads_only_after_the_server_opened_and_the_local_client_connected() {
        var bench = new Bench();
        bench.Flow.Host(bench.Request());
        Assert.Equal(1, bench.Server.Starts);
        Assert.Equal(0, bench.Client.Connects);

        bench.Server.Publish(ServerState.Open);
        Assert.Equal(1, bench.Client.Connects);
        Assert.Equal(0, bench.Loads);

        bench.Client.Connect();
        Assert.Equal(1, bench.Loads);
        Assert.Equal(HostingState.Loading, bench.Flow.State);

        bench.Screens.Load();
        Assert.Equal(HostingState.Hosting, bench.Flow.State);
        Assert.Empty(bench.Screens.Finishes);
    }

    [Fact]
    public void A_port_in_use_undoes_the_start_and_shows_why_without_loading() {
        var bench = new Bench();
        bench.Flow.Host(bench.Request());
        bench.Server.Publish(ServerState.Failed, ServerFailure.PortInUse);

        Assert.Equal(HostingState.Idle, bench.Flow.State);
        Assert.Equal(1, bench.Server.Stops);
        Assert.Equal(0, bench.Client.Connects);
        Assert.Equal(0, bench.Loads);
        Assert.Equal(new[] { (HostingFailure.PortInUse, false) }, bench.Screens.Finishes);
    }

    [Fact]
    public void A_local_client_that_cannot_connect_undoes_the_server_and_shows_why() {
        var bench = new Bench();
        bench.Flow.Host(bench.Request());
        bench.Server.Publish(ServerState.Open);
        bench.Client.Fail();

        Assert.Equal(HostingState.Idle, bench.Flow.State);
        Assert.Equal(1, bench.Client.Disconnects);
        Assert.Equal(1, bench.Server.Stops);
        Assert.Equal(0, bench.Loads);
        Assert.Equal(new[] { (HostingFailure.LocalConnectionFailed, false) }, bench.Screens.Finishes);
    }

    [Fact]
    public void A_server_failure_while_playing_returns_from_the_game_with_the_reason() {
        var bench = BenchHosting();
        bench.Server.Publish(ServerState.Failed, ServerFailure.LoopFailed);

        Assert.Equal(HostingState.Idle, bench.Flow.State);
        Assert.Equal(new[] { (HostingFailure.ServerFailed, true) }, bench.Screens.Finishes);
    }

    [Fact]
    public void A_player_who_leaves_is_told_nothing_and_late_reports_change_nothing() {
        var bench = BenchHosting();
        bench.Flow.Leave();
        Assert.Equal(new[] { (HostingFailure.Left, true) }, bench.Screens.Finishes);

        // The reports of the undone session arrive after it ended.
        bench.Server.Publish(ServerState.Stopped);
        bench.Client.Fail();
        bench.Client.Connect();
        Assert.Equal(HostingState.Idle, bench.Flow.State);
        Assert.Single(bench.Screens.Finishes);

        bench.Flow.Host(bench.Request());
        Assert.Equal(HostingState.StartingServer, bench.Flow.State);
        Assert.Equal(2, bench.Server.Starts);
    }

    [Fact]
    public void The_first_failure_is_the_one_shown() {
        var bench = new Bench();
        bench.Flow.Host(bench.Request());
        bench.Server.Publish(ServerState.Open);
        bench.Server.StopsLater = true;
        bench.Client.Fail();
        bench.Server.Publish(ServerState.Failed, ServerFailure.LoopFailed);
        bench.Server.Publish(ServerState.Stopped);

        Assert.Equal(new[] { (HostingFailure.LocalConnectionFailed, false) }, bench.Screens.Finishes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_reports_never_load_the_save_without_an_open_server_and_a_connected_client(int seed) {
        var random = new Random(seed);
        var bench = new Bench();
        var hosts = 0;

        for (var step = 0; step < 3000; step++) {
            bench.Server.StopsLater = random.Next(3) == 0;
            switch (random.Next(9)) {
                case 0:
                    if (bench.Flow.State == HostingState.Idle) hosts++;
                    bench.Flow.Host(bench.Request());
                    break;
                // The fakes only report what a real server and client could report in their state.
                case 1 when bench.Server.State == ServerState.Starting:
                    bench.Server.Publish(ServerState.Open);
                    break;
                case 2 when bench.Server.State == ServerState.Starting:
                    bench.Server.Publish(ServerState.Failed, ServerFailure.PortInUse);
                    break;
                case 3 when bench.Server.State == ServerState.Stopping:
                    bench.Server.Publish(ServerState.Stopped);
                    break;
                case 4 when bench.Client.IsConnecting:
                    bench.Client.Connect();
                    break;
                case 5 when bench.Client.IsConnecting || bench.Client.IsConnected:
                    bench.Client.Fail();
                    break;
                case 6:
                    bench.Screens.Load();
                    break;
                case 7:
                    bench.Flow.Leave();
                    break;
                case 8 when bench.Server.State == ServerState.Open:
                    bench.Server.Publish(ServerState.Failed, ServerFailure.LoopFailed);
                    break;
            }

            // Every hosting that went back to idle was finished exactly once.
            var finished = hosts - (bench.Flow.State == HostingState.Idle ? 0 : 1);
            Assert.Equal(finished, bench.Screens.Finishes.Count);
        }

        // S7: each load happened with the server open and the local client connected.
        Assert.All(bench.LoadConditions, condition => Assert.True(condition));
    }

    private static Bench BenchHosting() {
        var bench = new Bench();
        bench.Flow.Host(bench.Request());
        bench.Server.Publish(ServerState.Open);
        bench.Client.Connect();
        bench.Screens.Load();
        Assert.Equal(HostingState.Hosting, bench.Flow.State);
        return bench;
    }

    private sealed class Bench {
        public FakeServer Server { get; } = new();
        public FakeClient Client { get; } = new();
        public FakeScreens Screens { get; } = new();
        public HostingFlow Flow { get; }
        public int Loads { get; private set; }
        public List<bool> LoadConditions { get; } = new();

        public Bench() {
            Flow = new HostingFlow(Server, Client, Screens);
        }

        public HostingRequest Request() => new(26950, "Host", false, () => {
            Loads++;
            LoadConditions.Add(Server.State == ServerState.Open && Client.IsConnected);
        });
    }

    private sealed class FakeServer : IHostedServer {
        public event Action<ServerState, ServerFailure> StateChanged;
        public ServerState State { get; private set; } = ServerState.Stopped;
        public int Starts { get; private set; }
        public int Stops { get; private set; }

        /// <summary>When set, a stop is reported later instead of being complete on return.</summary>
        public bool StopsLater { get; set; }

        public void Start(int port, bool newGame) {
            Starts++;
            State = ServerState.Starting;
        }

        public void Stop() {
            Stops++;
            State = StopsLater ? ServerState.Stopping : ServerState.Stopped;
        }

        public void Publish(ServerState state, ServerFailure failure = ServerFailure.None) {
            State = state;
            StateChanged?.Invoke(state, failure);
        }
    }

    private sealed class FakeClient : ILocalClient {
        public event Action Connected;
        public event Action Failed;
        public bool IsConnecting { get; private set; }
        public bool IsConnected { get; private set; }
        public int Connects { get; private set; }
        public int Disconnects { get; private set; }

        void ILocalClient.Connect(int port, string username) {
            Connects++;
            IsConnecting = true;
        }

        public void Disconnect() {
            Disconnects++;
            IsConnecting = false;
            IsConnected = false;
        }

        public void Connect() {
            IsConnecting = false;
            IsConnected = true;
            Connected?.Invoke();
        }

        public void Fail() {
            IsConnecting = false;
            IsConnected = false;
            Failed?.Invoke();
        }
    }

    private sealed class FakeScreens : IHostScreens {
        public event Action GameLoaded;
        public List<(HostingFailure, bool)> Finishes { get; } = new();

        public void Finish(HostingFailure reason, bool gameLoaded) => Finishes.Add((reason, gameLoaded));

        public void Load() => GameLoaded?.Invoke();
    }
}
