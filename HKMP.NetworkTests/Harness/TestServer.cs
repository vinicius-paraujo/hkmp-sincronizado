using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Hkmp.Networking.Timing;
using Hkmp.Networking.Server;
using Hkmp.Networking.Server.Transport;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// The real DTLS transport of the HKMP server on a free loopback port, recording which endpoint each received text
/// came from.
/// </summary>
internal sealed class TestServer : IDisposable {
    private readonly ServerTransport _server = new(ConnectionTimeouts.Default);
    private readonly ConcurrentQueue<(IPEndPoint From, string Text)> _received = new();

    public int Port { get; } = Hkmp.FreeUdpPort();

    public TestServer() {
        _server.DataReceivedEvent += Record;
        Start();
    }

    public void Start() => _server.Start(Port);

    public void Stop() => _server.Stop();

    public void Disconnect(IPEndPoint client) => _server.DisconnectClient(client);

    /// <summary>
    /// The endpoints the server has registered as connected clients.
    /// </summary>
    public IReadOnlyList<IPEndPoint> Clients => _server.Clients.Select(client => client.EndPoint).ToList();

    /// <summary>
    /// The handshakes the server is running now.
    /// </summary>
    public string AcceptorState => $"{_server.HandshakesRunning} handshakes running";

    /// <summary>
    /// Sends the text to every registered client, as the server's periodic updates do. Errors are counted, not
    /// thrown, because a client that has gone away is part of what is being tested.
    /// </summary>
    public int SendToAll(string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        var errors = 0;
        foreach (var client in _server.Clients) {
            try {
                client.DtlsTransport.Send(bytes, 0, bytes.Length);
            } catch (Exception) {
                errors++;
            }
        }

        return errors;
    }

    /// <summary>
    /// Waits until the text arrives and returns the endpoint the server attributed it to, or null on timeout.
    /// </summary>
    public IPEndPoint WaitFor(string text, TimeSpan timeout) {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout) {
            foreach (var (from, received) in _received) {
                if (received == text) return from;
            }

            Thread.Sleep(10);
        }

        return null;
    }

    private void Record(DtlsServerClient client, byte[] buffer, int length) {
        _received.Enqueue((client.EndPoint, Encoding.UTF8.GetString(buffer, 0, length)));
    }

    public void Dispose() {
        try {
            Stop();
        } catch (Exception) {
            // A server whose threads already died may fail to stop cleanly; the port is released either way.
        }
    }
}
