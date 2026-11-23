using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Networking.Timing;
using Hkmp.Networking.Server;
using Hkmp.Networking.Server.Transport;
using Hkmp.NetworkTests.Harness;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Xunit;

namespace Hkmp.NetworkTests;

/// <summary>
/// The parts of the server transport, each on its own: the peer automaton, the record check, the cookie gate,
/// the handshake pool, the demultiplexer and the supervisor.
/// </summary>
public class TransportPartsTests {
    public static IEnumerable<object[]> ListedTransitions() {
        object[] Row(PeerState state, PeerEvent peerEvent, PeerState next, PeerAction action) =>
            [state, peerEvent, next, action];

        yield return Row(PeerState.Handshake, PeerEvent.Datagram, PeerState.Handshake, PeerAction.DeliverToHandshake);
        yield return Row(PeerState.Handshake, PeerEvent.NewClientHello, PeerState.Handshake, PeerAction.DeliverToHandshake);
        yield return Row(PeerState.Handshake, PeerEvent.HandshakeSucceeded, PeerState.Established, PeerAction.AnnounceEstablished);
        yield return Row(PeerState.Handshake, PeerEvent.HandshakeFailed, PeerState.Closed, PeerAction.AnnounceFailure);
        yield return Row(PeerState.Handshake, PeerEvent.HandshakeDeadline, PeerState.Closed, PeerAction.AnnounceFailure);
        yield return Row(PeerState.Handshake, PeerEvent.Close, PeerState.Closed, PeerAction.CancelHandshake);
        yield return Row(PeerState.Established, PeerEvent.Datagram, PeerState.Established, PeerAction.Decrypt);
        yield return Row(PeerState.Established, PeerEvent.NewClientHello, PeerState.Closed, PeerAction.Replace);
        yield return Row(PeerState.Established, PeerEvent.Close, PeerState.Closed, PeerAction.CloseTransport);
    }

    [Theory]
    [MemberData(nameof(ListedTransitions))]
    public void Each_row_of_the_peer_table_holds(object state, object peerEvent, object next, object action) {
        // The automaton's types are internal, so the rows reach the test as objects.
        Assert.Equal(new PeerTransition((PeerState) next, (PeerAction) action),
            PeerAutomaton.Next((PeerState) state, (PeerEvent) peerEvent));
    }

    [Fact]
    public void Every_unlisted_event_keeps_the_state_and_does_nothing() {
        var listed = new HashSet<(PeerState, PeerEvent)>();
        foreach (var row in ListedTransitions()) listed.Add(((PeerState) row[0], (PeerEvent) row[1]));

        foreach (PeerState state in Enum.GetValues(typeof(PeerState))) {
            foreach (PeerEvent peerEvent in Enum.GetValues(typeof(PeerEvent))) {
                if (listed.Contains((state, peerEvent))) continue;
                Assert.Equal(new PeerTransition(state, PeerAction.None), PeerAutomaton.Next(state, peerEvent));
            }
        }
    }

    [Fact]
    public void Only_an_unencrypted_client_hello_counts_as_one() {
        var hello = Record(contentType: 22, epoch: 0, messageType: 1);
        Assert.True(DtlsRecord.IsClientHello(hello, hello.Length));

        var applicationData = Record(contentType: 23, epoch: 1, messageType: 0);
        var finished = Record(contentType: 22, epoch: 1, messageType: 1);
        var serverHello = Record(contentType: 22, epoch: 0, messageType: 2);
        Assert.False(DtlsRecord.IsClientHello(applicationData, applicationData.Length));
        Assert.False(DtlsRecord.IsClientHello(finished, finished.Length));
        Assert.False(DtlsRecord.IsClientHello(serverHello, serverHello.Length));
        Assert.False(DtlsRecord.IsClientHello(hello, 13));
    }

    [Fact]
    public void A_first_client_hello_gets_a_hello_verify_request_and_no_handshake() {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var (hello, client) = CaptureClientHello(((IPEndPoint) server.LocalEndPoint).Port);
        using var clientSocket = client;

        var gate = new CookieGate(new BcTlsCrypto(), server);
        var request = gate.Verify((IPEndPoint) clientSocket.LocalEndPoint, hello, hello.Length);

        Assert.Null(request);
        clientSocket.ReceiveTimeout = 2000;
        var reply = new byte[2048];
        var length = clientSocket.Receive(reply);
        Assert.True(length > 13 && reply[0] == 22 && reply[13] == 3, "The answer is not a hello verify request.");
    }

    [Fact]
    public void Anything_but_a_client_hello_passes_the_gate_silently() {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var gate = new CookieGate(new BcTlsCrypto(), server);
        var data = Record(contentType: 23, epoch: 1, messageType: 0);

        Assert.Null(gate.Verify(new IPEndPoint(IPAddress.Loopback, 40100), data, data.Length));
    }

    [Fact]
    public void A_full_pool_refuses_the_next_handshake() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var pool = new HandshakePool();
        var peers = new List<Peer>();
        var finished = new CountdownEvent(HandshakePool.Capacity);

        try {
            for (var i = 0; i < HandshakePool.Capacity; i++) {
                var peer = NewPeer(socket, 40200 + i);
                peers.Add(peer);
                Assert.True(pool.TryBegin(peer, null, (_, _, _) => finished.Signal()));
            }

            Assert.False(pool.TryBegin(NewPeer(socket, 40299), null, (_, _, _) => { }));
            Assert.Equal(HandshakePool.Capacity, pool.Running);
        } finally {
            // A closed transport ends its waiting handshake at once.
            peers.ForEach(peer => peer.Transport.Close());
        }

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "Closing the transports did not end the handshakes.");
        Assert.Equal(0, pool.Running);
    }

    [Fact]
    public void A_late_datagram_from_an_unknown_endpoint_creates_no_peer() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var demux = new PeerDemux(socket, new CookieGate(new BcTlsCrypto(), socket), new HandshakePool(),
            new NetworkCommands(), new NoListener(), () => new ServerTlsServer(new BcTlsCrypto(), ConnectionTimeouts.Default.ServerHandshake));
        var late = Record(contentType: 23, epoch: 1, messageType: 0);

        demux.Route(new IPEndPoint(IPAddress.Loopback, 40300), late, late.Length);

        Assert.Equal(0, demux.PeerCount);
    }

    [Fact]
    public void The_supervisor_restarts_a_failed_loop() {
        var supervisor = new Supervisor();
        var runs = 0;
        using var done = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();

        supervisor.Start("test loop", token => {
            if (Interlocked.Increment(ref runs) < 3) throw new InvalidOperationException("failure");
            done.Set();
            token.WaitHandle.WaitOne();
        }, stop.Token);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), $"The loop ran {runs} times.");
        stop.Cancel();
        Assert.True(supervisor.Join(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void The_supervisor_gives_up_on_a_loop_that_keeps_failing() {
        var supervisor = new Supervisor();
        Exception reported = null;
        supervisor.Faulted += (_, e) => reported = e;
        using var stop = new CancellationTokenSource();

        supervisor.Start("failing loop", _ => throw new InvalidOperationException("always"), stop.Token);

        Assert.True(supervisor.Join(TimeSpan.FromSeconds(10)), "The supervisor kept restarting the loop.");
        Assert.IsType<InvalidOperationException>(reported);
    }

    /// <summary>
    /// The first datagram a real HKMP client sends, which is its client hello without a cookie. The client gets
    /// no answer and gives up later on its own thread.
    /// </summary>
    private static (byte[] Hello, Socket Client) CaptureClientHello(int port) {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.ReceiveTimeout = 5000;
        var client = new TestClient("hello");
        new Thread(() => client.Connect(((IPEndPoint) listener.LocalEndPoint).Port)) { IsBackground = true }.Start();

        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var length = listener.ReceiveFrom(buffer, ref from);
        client.Dispose();

        // A socket of the test's own, bound where the cookie answer can be read back.
        var replyTo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        replyTo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var hello = new byte[length];
        Array.Copy(buffer, hello, length);
        return (hello, replyTo);
    }

    private static Peer NewPeer(Socket socket, int port) {
        var endPoint = new IPEndPoint(IPAddress.Loopback, port);
        return new Peer(endPoint, new PeerTransport(socket, endPoint), new ServerTlsServer(new BcTlsCrypto(), ConnectionTimeouts.Default.ServerHandshake));
    }

    private static byte[] Record(byte contentType, byte epoch, byte messageType) {
        var record = new byte[40];
        record[0] = contentType;
        record[1] = 254;
        record[2] = 253;
        record[4] = epoch;
        record[13] = messageType;
        return record;
    }

    private sealed class NoListener : IPeerListener {
        public void OnEstablished(DtlsServerClient client) { }
        public void OnData(DtlsServerClient client, byte[] buffer, int length) { }
        public void OnClosed(DtlsServerClient client) { }
    }
}
