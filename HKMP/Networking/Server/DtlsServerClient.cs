using System.Net;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server;

/// <summary>
/// Data class containing the related object instances for a DTLS server client.
/// </summary>
internal class DtlsServerClient {
    /// <summary>
    /// The fragments received from this client so far.
    /// </summary>
    public Packet.PacketFragments Fragments { get; } = new Packet.PacketFragments();

    /// <summary>
    /// The DTLS transport instance.
    /// </summary>
    public DtlsTransport DtlsTransport { get; }

    /// <summary>
    /// The IP endpoint of the client.
    /// </summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Construct the client from its encrypted transport and endpoint.
    /// </summary>
    /// <param name="dtlsTransport">The DTLS transport, or null for a client without a session.</param>
    /// <param name="endPoint">The IP endpoint of the client.</param>
    public DtlsServerClient(DtlsTransport dtlsTransport, IPEndPoint endPoint) {
        DtlsTransport = dtlsTransport;
        EndPoint = endPoint;
    }
}
