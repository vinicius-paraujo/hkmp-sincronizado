using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Server;
using Hkmp.NetworkTests.Harness;
using Xunit;
using Xunit.Abstractions;
using HkmpAssembly = Hkmp.NetworkTests.Harness.Hkmp;

namespace Hkmp.NetworkTests;

/// <summary>
/// Admission above the transport: how the server reports players that join, leave or fail to start.
/// </summary>
public class ServerAdmissionTests {
    private static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public ServerAdmissionTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public void An_accepted_player_that_vanishes_before_registering_is_reported_as_gone() {
        var server = new NetServer(new PacketManager());
        server.ConnectionRequestEvent += (_, _, serverInfo) => serverInfo.ConnectionResult = ServerConnectionResult.Accepted;
        var timedOut = new ConcurrentQueue<ushort>();
        server.ClientTimeoutEvent += timedOut.Enqueue;

        var client = server.CreateNewClient(NewTransportClient(40001));
        try {
            server.OnConnectionRequest(client.Id, NewClientInfo(), new ServerInfo());
            Assert.False(client.IsRegistered);

            server.HandleClientTimeout(client);
        } finally {
            client.Disconnect();
        }

        Assert.Equal(new[] { client.Id }, timedOut.ToArray());
    }

    [Fact]
    public void A_player_that_vanishes_before_any_answer_is_not_reported() {
        var server = new NetServer(new PacketManager());
        var timedOut = new ConcurrentQueue<ushort>();
        server.ClientTimeoutEvent += timedOut.Enqueue;

        var client = server.CreateNewClient(NewTransportClient(40002));
        try {
            server.HandleClientTimeout(client);
        } finally {
            client.Disconnect();
        }

        Assert.Empty(timedOut);
    }

    [Fact]
    public void A_throttled_player_does_not_keep_out_others_on_the_same_address() {
        var server = new NetServer(new PacketManager());
        var refused = new IPEndPoint(IPAddress.Loopback, 40003);
        var neighbour = new IPEndPoint(IPAddress.Loopback, 40004);

        server.Throttle(refused);

        Assert.True(server.IsThrottled(refused));
        Assert.False(server.IsThrottled(neighbour));
    }

    [Fact]
    public void Datagrams_of_an_ended_session_do_not_take_over_the_next_handshake() {
        using var server = new TestServer();
        using var stray = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        stray.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, server.Port);

        // Application data records, as a client whose session ended keeps sending for a moment
        var record = new byte[64];
        record[0] = 23;
        record[1] = 254;
        record[2] = 253;
        using var stop = new CancellationTokenSource();
        var sender = new Thread(() => {
            while (!stop.IsCancellationRequested) {
                stray.SendTo(record, target);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        sender.Start();

        using var next = new TestClient("next");
        bool joined;
        try {
            joined = next.Connect(server.Port) && DeliversAsItself(server, next);
        } finally {
            stop.Cancel();
            sender.Join();
        }

        _output.WriteLine($"{next.Describe()}; data attributed correctly: {joined}");
        Assert.True(joined, "Datagrams from an ended session kept a valid player out.");
    }

    [Fact]
    public void Data_from_a_disconnected_client_does_not_reach_the_server() {
        using var server = new TestServer();
        using var client = new TestClient("disconnected");
        Assert.True(client.Connect(server.Port), client.Describe());

        // The server disconnects only clients it has heard from, so the session is first seen to work.
        Assert.True(DeliversAsItself(server, client));
        server.Disconnect(client.LocalEndPoint);
        client.Send("after the disconnect");

        Assert.Null(server.WaitFor("after the disconnect", TimeSpan.FromSeconds(1)));
    }

    private bool DeliversAsItself(TestServer server, TestClient client) {
        var text = $"{client.Name}-{Guid.NewGuid():N}";
        client.Send(text);
        var from = server.WaitFor(text, DataTimeout);
        _output.WriteLine($"  sent from {client.LocalEndPoint}, received from {from?.ToString() ?? "nowhere"}");
        return from != null && from.Equals(client.LocalEndPoint);
    }

    private static DtlsServerClient NewTransportClient(int port) =>
        new(null, new IPEndPoint(IPAddress.Loopback, port));

    private static ClientInfo NewClientInfo() => new() {
        Username = "player",
        AuthKey = "key",
        AddonData = new()
    };
}
