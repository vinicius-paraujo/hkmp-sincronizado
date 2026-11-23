using System.Net;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// An endpoint with a handshake in progress or an established DTLS session. Only the network thread changes it.
/// </summary>
internal sealed class Peer
{
    /// <summary>
    /// Creates a peer in the handshake state.
    /// </summary>
    /// <param name="endPoint">The endpoint of the peer.</param>
    /// <param name="transport">The datagram transport of the peer.</param>
    /// <param name="tlsServer">The TLS server that runs this peer's handshake, used by no other peer.</param>
    public Peer(IPEndPoint endPoint, PeerTransport transport, ServerTlsServer tlsServer)
    {
        EndPoint = endPoint;
        Transport = transport;
        TlsServer = tlsServer;
    }

    /// <summary>
    /// The endpoint of the peer.
    /// </summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// The datagram transport of the peer.
    /// </summary>
    public PeerTransport Transport { get; }

    /// <summary>
    /// The TLS server of the peer's handshake.
    /// </summary>
    public ServerTlsServer TlsServer { get; }

    /// <summary>
    /// The current state of the peer.
    /// </summary>
    public PeerState State { get; private set; } = PeerState.Handshake;

    /// <summary>
    /// The client the server sees once the handshake succeeded; null before that.
    /// </summary>
    public DtlsServerClient Client { get; private set; }

    /// <summary>
    /// Applies an event to the peer.
    /// </summary>
    /// <param name="peerEvent">The event.</param>
    /// <returns>The action the owner performs for the transition.</returns>
    public PeerAction Apply(PeerEvent peerEvent)
    {
        PeerTransition transition = PeerAutomaton.Next(State, peerEvent);
        State = transition.Next;
        return transition.Action;
    }

    /// <summary>
    /// Records the encrypted transport of a successful handshake.
    /// </summary>
    /// <param name="dtlsTransport">The transport the handshake produced.</param>
    public void Establish(DtlsTransport dtlsTransport)
    {
        Transport.MarkEstablished();
        Client = new DtlsServerClient(dtlsTransport, EndPoint);
    }
}
