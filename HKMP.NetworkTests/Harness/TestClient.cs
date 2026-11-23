using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// The real DTLS client of HKMP. Each instance opens its own socket, as a game does on every connection.
/// </summary>
internal sealed class TestClient : IDisposable {
    private static readonly Type ClientType = Hkmp.Type("Hkmp.Networking.Client.DtlsClient");

    private readonly object _client = Activator.CreateInstance(ClientType, Networking.Timing.ConnectionTimeouts.Default.ClientHandshake);

    public string Name { get; }

    /// <summary>
    /// The time the last connection attempt took, successful or not.
    /// </summary>
    public TimeSpan ConnectTime { get; private set; }

    /// <summary>
    /// Why the last connection attempt failed, or null.
    /// </summary>
    public Exception Error { get; private set; }

    public TestClient(string name) {
        Name = name;
    }

    /// <summary>
    /// Connects to the port on the loopback interface. Blocks for up to the client's handshake timeout.
    /// </summary>
    public bool Connect(int port) {
        var clock = Stopwatch.StartNew();
        try {
            Hkmp.Call(_client, "Connect", "127.0.0.1", port);
            Error = null;
            return true;
        } catch (Exception e) {
            Error = e;
            return false;
        } finally {
            ConnectTime = clock.Elapsed;
        }
    }

    /// <summary>
    /// The local endpoint of the client's socket, which is the source address the server or a proxy sees.
    /// </summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint) ((Socket) Hkmp.Field(_client, "_socket"))?.LocalEndPoint;

    public void Send(string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        Hkmp.Call(Hkmp.Property(_client, "DtlsTransport"), "Send", bytes, 0, bytes.Length);
    }

    public string Describe() => Error == null
        ? $"{Name}: connected in {ConnectTime.TotalSeconds:0.0} s"
        : $"{Name}: failed after {ConnectTime.TotalSeconds:0.0} s ({Error.GetType().Name}: {Error.Message})";

    public void Dispose() {
        try {
            Hkmp.Call(_client, "Disconnect");
        } catch (Exception) {
            // Disconnecting a client whose connection failed is allowed to fail.
        }
    }
}
