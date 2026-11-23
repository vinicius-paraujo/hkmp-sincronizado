using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Server;
using Hkmp.Networking.Server.Lifecycle;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// The lifecycle of the server: its automaton, the lifecycle over a fake port, and the real server on a port that
/// another program holds.
/// </summary>
public class ServerLifecycleTests {
    public static IEnumerable<object[]> ListedTransitions() {
        object[] Row(ServerState state, ServerLifecycleEvent lifecycleEvent, ServerState next, ServerLifecycleAction action) =>
            [state, lifecycleEvent, next, action];

        yield return Row(ServerState.Stopped, ServerLifecycleEvent.Start, ServerState.Starting, ServerLifecycleAction.OpenPort);
        yield return Row(ServerState.Starting, ServerLifecycleEvent.PortOpened, ServerState.Open, ServerLifecycleAction.Publish);
        yield return Row(ServerState.Starting, ServerLifecycleEvent.PortRefused, ServerState.Failed, ServerLifecycleAction.RecordFailure);
        yield return Row(ServerState.Starting, ServerLifecycleEvent.LoopFailed, ServerState.Failed, ServerLifecycleAction.RecordFailure);
        yield return Row(ServerState.Starting, ServerLifecycleEvent.Stop, ServerState.Stopping, ServerLifecycleAction.EndThreads);
        yield return Row(ServerState.Open, ServerLifecycleEvent.LoopFailed, ServerState.Failed, ServerLifecycleAction.RecordFailureAndEndSessions);
        yield return Row(ServerState.Open, ServerLifecycleEvent.Stop, ServerState.Stopping, ServerLifecycleAction.EndSessionsAndThreads);
        yield return Row(ServerState.Failed, ServerLifecycleEvent.Stop, ServerState.Stopping, ServerLifecycleAction.EndThreads);
        yield return Row(ServerState.Stopping, ServerLifecycleEvent.Ended, ServerState.Stopped, ServerLifecycleAction.Publish);
        yield return Row(ServerState.Stopping, ServerLifecycleEvent.StopDeadline, ServerState.Stopped, ServerLifecycleAction.RecordLingeringThreads);
    }

    [Theory]
    [MemberData(nameof(ListedTransitions))]
    public void Each_row_of_the_server_table_holds(object state, object lifecycleEvent, object next, object action) {
        Assert.Equal(new ServerLifecycleTransition((ServerState) next, (ServerLifecycleAction) action),
            ServerLifecycleAutomaton.Next((ServerState) state, (ServerLifecycleEvent) lifecycleEvent));
    }

    [Fact]
    public void Every_unlisted_event_keeps_the_state_and_does_nothing() {
        var listed = new HashSet<(ServerState, ServerLifecycleEvent)>();
        foreach (var row in ListedTransitions()) listed.Add(((ServerState) row[0], (ServerLifecycleEvent) row[1]));

        foreach (ServerState state in Enum.GetValues(typeof(ServerState))) {
            foreach (ServerLifecycleEvent lifecycleEvent in Enum.GetValues(typeof(ServerLifecycleEvent))) {
                if (listed.Contains((state, lifecycleEvent))) continue;
                Assert.Equal(new ServerLifecycleTransition(state, ServerLifecycleAction.None),
                    ServerLifecycleAutomaton.Next(state, lifecycleEvent));
            }
        }
    }

    [Fact]
    public void A_port_in_use_fails_with_its_reason_and_a_later_start_opens() {
        var port = new FakePort { Refusal = SocketError.AddressAlreadyInUse };
        var lifecycle = new ServerLifecycle(port, () => { });
        var published = Record(lifecycle);

        lifecycle.Start(26950);
        Assert.Equal(ServerState.Failed, lifecycle.State);
        Assert.Equal(ServerFailure.PortInUse, lifecycle.Failure);

        lifecycle.Stop();
        port.Refusal = null;
        lifecycle.Start(26950);

        Assert.Equal(ServerState.Open, lifecycle.State);
        Assert.Equal(ServerFailure.None, lifecycle.Failure);
        Assert.Equal(
            new[] { (ServerState.Failed, ServerFailure.PortInUse), (ServerState.Stopped, ServerFailure.None), (ServerState.Open, ServerFailure.None) },
            published);
    }

    [Fact]
    public void Another_refusal_of_the_port_is_reported_as_unavailable() {
        var lifecycle = new ServerLifecycle(new FakePort { Refusal = SocketError.AccessDenied }, () => { });
        lifecycle.Start(26950);
        Assert.Equal(ServerFailure.PortUnavailable, lifecycle.Failure);
    }

    [Fact]
    public void A_loop_that_fails_while_open_ends_the_sessions_and_the_server_stops_on_request() {
        var sessionsEnded = 0;
        var port = new FakePort();
        var lifecycle = new ServerLifecycle(port, () => sessionsEnded++);
        lifecycle.Start(26950);

        lifecycle.OnLoopFailed();
        Assert.Equal(ServerState.Failed, lifecycle.State);
        Assert.Equal(ServerFailure.LoopFailed, lifecycle.Failure);
        Assert.Equal(1, sessionsEnded);

        lifecycle.Stop();
        Assert.Equal(ServerState.Stopped, lifecycle.State);
        Assert.Equal(1, port.Closes);
        Assert.Equal(1, sessionsEnded);
    }

    [Fact]
    public void A_thread_that_outlives_the_stop_deadline_still_leaves_the_server_stopped() {
        var lifecycle = new ServerLifecycle(new FakePort { EndsInTime = false }, () => { });
        lifecycle.Start(26950);
        lifecycle.Stop();
        Assert.Equal(ServerState.Stopped, lifecycle.State);
    }

    [Fact]
    public void A_late_loop_failure_does_not_mark_a_stopped_server() {
        var lifecycle = new ServerLifecycle(new FakePort(), () => { });
        var published = Record(lifecycle);
        lifecycle.OnLoopFailed();
        Assert.Equal(ServerState.Stopped, lifecycle.State);
        Assert.Equal(ServerFailure.None, lifecycle.Failure);
        Assert.Empty(published);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_starts_failures_and_stops_always_end_stopped_and_open_only_with_the_port_open(int seed) {
        var random = new Random(seed);
        var port = new FakePort();
        var lifecycle = new ServerLifecycle(port, () => { });

        for (var step = 0; step < 2000; step++) {
            port.Refusal = random.Next(3) == 0 ? SocketError.AddressAlreadyInUse : null;
            port.EndsInTime = random.Next(4) != 0;
            switch (random.Next(3)) {
                case 0:
                    lifecycle.Start(26950);
                    break;
                case 1:
                    lifecycle.OnLoopFailed();
                    break;
                case 2:
                    lifecycle.Stop();
                    // L5: a stop always reaches stopped, whether or not the threads ended in time.
                    Assert.Equal(ServerState.Stopped, lifecycle.State);
                    Assert.False(port.IsOpen);
                    break;
            }

            Assert.Equal(lifecycle.State == ServerState.Open, port.IsOpen && lifecycle.Failure == ServerFailure.None);
            Assert.True(lifecycle.State is ServerState.Stopped or ServerState.Open or ServerState.Failed);
            Assert.Equal(lifecycle.State == ServerState.Failed, lifecycle.Failure != ServerFailure.None);
        }
    }

    [Fact]
    public void The_real_server_on_a_port_in_use_fails_and_opens_once_the_port_is_free() {
        var portNumber = Harness.Hkmp.FreeUdpPort();
        var server = new NetServer(new PacketManager());
        var published = new List<(ServerState, ServerFailure)>();
        server.LifecycleChanged += (state, failure) => published.Add((state, failure));

        using (var occupant = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
            occupant.ExclusiveAddressUse = true;
            occupant.Bind(new IPEndPoint(IPAddress.Any, portNumber));

            server.Start(portNumber);
            Assert.Equal(ServerState.Failed, server.State);
            Assert.Equal(ServerFailure.PortInUse, server.Failure);
            Assert.False(server.IsStarted);
            server.Stop();
            Assert.Equal(ServerState.Stopped, server.State);
        }

        server.Start(portNumber);
        try {
            Assert.Equal(ServerState.Open, server.State);
            Assert.True(server.IsStarted);
        } finally {
            server.Stop();
        }

        Assert.Equal(new[] {
            (ServerState.Failed, ServerFailure.PortInUse),
            (ServerState.Stopped, ServerFailure.None),
            (ServerState.Open, ServerFailure.None),
            (ServerState.Stopped, ServerFailure.None),
        }, published);
    }

    private static List<(ServerState, ServerFailure)> Record(ServerLifecycle lifecycle) {
        var published = new List<(ServerState, ServerFailure)>();
        lifecycle.StateChanged += (state, failure) => published.Add((state, failure));
        return published;
    }

    private sealed class FakePort : IServerPort {
        public SocketError? Refusal { get; set; }
        public bool EndsInTime { get; set; } = true;
        public bool IsOpen { get; private set; }
        public int Closes { get; private set; }

        public void Open(int port) {
            if (Refusal is { } error) throw new SocketException((int) error);
            IsOpen = true;
        }

        public bool Close() {
            Closes++;
            IsOpen = false;
            return EndsInTime;
        }
    }
}
