using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Hands every datagram to the peer of its endpoint and performs the actions of the peer automaton. An endpoint
/// without a peer gets one only through a verified client hello, so a late datagram of a peer that ended is
/// dropped without creating state. Only the network thread calls it.
/// </summary>
internal sealed class PeerDemux
{
    private readonly Dictionary<IPEndPoint, Peer> _peers = new();
    private readonly Socket _socket;
    private readonly CookieGate _gate;
    private readonly HandshakePool _pool;
    private readonly NetworkCommands _commands;
    private readonly IPeerListener _listener;
    private readonly Func<ServerTlsServer> _newTlsServer;

    /// <summary>
    /// Creates the demultiplexer.
    /// </summary>
    /// <param name="socket">The server socket shared by all peers.</param>
    /// <param name="gate">The cookie check for client hellos from endpoints without a peer.</param>
    /// <param name="pool">The pool that runs the handshakes.</param>
    /// <param name="commands">The inbox of the network thread, where handshake results return.</param>
    /// <param name="listener">Receives what happens to established peers.</param>
    /// <param name="newTlsServer">Creates the TLS server of one handshake.</param>
    public PeerDemux(
        Socket socket,
        CookieGate gate,
        HandshakePool pool,
        NetworkCommands commands,
        IPeerListener listener,
        Func<ServerTlsServer> newTlsServer)
    {
        _socket = socket;
        _gate = gate;
        _pool = pool;
        _commands = commands;
        _listener = listener;
        _newTlsServer = newTlsServer;
    }

    /// <summary>
    /// The number of endpoints that have a peer, in handshake or established.
    /// </summary>
    public int PeerCount => _peers.Count;

    /// <summary>
    /// Routes a datagram received on the socket.
    /// </summary>
    /// <param name="from">The endpoint it came from.</param>
    /// <param name="buffer">The receive buffer, which is reused after the call returns.</param>
    /// <param name="length">The number of bytes received.</param>
    public void Route(IPEndPoint from, byte[] buffer, int length)
    {
        if (!_peers.TryGetValue(from, out Peer peer))
        {
            StartPeer(from, buffer, length);
            return;
        }

        if (peer.State == PeerState.Established && DtlsRecord.IsClientHello(buffer, length))
        {
            // The same address started over, as a client behind a NAT that kept its port does. Only a client
            // hello with a valid cookie may end the session that is there.
            DtlsRequest request = _gate.Verify(from, buffer, length);
            if (request != null)
            {
                Perform(peer, peer.Apply(PeerEvent.NewClientHello), null);
                Begin(from, request);
            }

            return;
        }

        Perform(peer, peer.Apply(PeerEvent.Datagram), Copy(buffer, length));
    }

    /// <summary>
    /// Ends the peer of the endpoint, if there is one.
    /// </summary>
    /// <param name="endPoint">The endpoint.</param>
    public void Close(IPEndPoint endPoint)
    {
        if (_peers.TryGetValue(endPoint, out Peer peer))
        {
            Perform(peer, peer.Apply(PeerEvent.Close), null);
        }
    }

    /// <summary>
    /// Ends the peer of the client, if it is still the endpoint's peer. A newer peer on the same endpoint stays.
    /// </summary>
    /// <param name="client">The established client of the peer.</param>
    public void Close(DtlsServerClient client)
    {
        if (_peers.TryGetValue(client.EndPoint, out Peer peer) && peer.Client == client)
        {
            Perform(peer, peer.Apply(PeerEvent.Close), null);
        }
    }

    /// <summary>
    /// Ends every peer.
    /// </summary>
    public void CloseAll()
    {
        foreach (Peer peer in new List<Peer>(_peers.Values))
        {
            Perform(peer, peer.Apply(PeerEvent.Close), null);
        }
    }

    private void StartPeer(IPEndPoint from, byte[] buffer, int length)
    {
        DtlsRequest request = _gate.Verify(from, buffer, length);
        if (request != null)
        {
            Begin(from, request);
        }
    }

    private void Begin(IPEndPoint endPoint, DtlsRequest request)
    {
        Peer peer = new(endPoint, new PeerTransport(_socket, endPoint), _newTlsServer());
        if (!_pool.TryBegin(peer, request, OnHandshakeFinished))
        {
            Logger.Debug($"Handshake pool is full, dropped the client hello of {endPoint}");
            return;
        }

        _peers[endPoint] = peer;
    }

    /// <summary>
    /// Runs on the handshake thread, so the result goes back to the network thread as a command.
    /// </summary>
    private void OnHandshakeFinished(Peer peer, DtlsTransport transport, Exception failure)
    {
        _commands.Post(() => ApplyHandshakeResult(peer, transport, failure));
    }

    private void ApplyHandshakeResult(Peer peer, DtlsTransport transport, Exception failure)
    {
        bool current = _peers.TryGetValue(peer.EndPoint, out Peer registered) && registered == peer;
        if (!current || peer.State != PeerState.Handshake)
        {
            // The peer was closed or replaced while its handshake ran; its session must not survive.
            transport?.Close();
            return;
        }

        if (failure != null)
        {
            PeerEvent outcome = failure is TlsTimeoutException ? PeerEvent.HandshakeDeadline : PeerEvent.HandshakeFailed;
            Logger.Info($"Handshake with {peer.EndPoint} ended without a session: {failure.Message}");
            Perform(peer, peer.Apply(outcome), null);
            return;
        }

        peer.Establish(transport);
        Perform(peer, peer.Apply(PeerEvent.HandshakeSucceeded), null);
    }

    private void Perform(Peer peer, PeerAction action, byte[] datagram)
    {
        switch (action)
        {
            case PeerAction.None:
                break;
            case PeerAction.DeliverToHandshake:
                peer.Transport.Deliver(datagram);
                break;
            case PeerAction.AnnounceEstablished:
                Logger.Debug($"Accepted DTLS connection on socket, endpoint: {peer.EndPoint}");
                _listener.OnEstablished(peer.Client);

                // The client may send data as soon as its own side finishes, before this result reached the
                // network thread; that data already waits in the transport.
                Decrypt(peer);
                break;
            case PeerAction.AnnounceFailure:
                Remove(peer);
                break;
            case PeerAction.CancelHandshake:
                peer.TlsServer.Cancel();
                peer.Transport.Close();
                Remove(peer);
                break;
            case PeerAction.Decrypt:
                peer.Transport.Deliver(datagram);
                Decrypt(peer);
                break;
            case PeerAction.Replace:
            case PeerAction.CloseTransport:
                CloseSession(peer);
                Remove(peer);
                _listener.OnClosed(peer.Client);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    /// <summary>
    /// Reads every record the delivered datagram holds. Reading only while datagrams are pending keeps the
    /// network thread from waiting on a peer that has nothing more.
    /// </summary>
    private void Decrypt(Peer peer)
    {
        DtlsTransport dtls = peer.Client.DtlsTransport;
        while (peer.Transport.HasPending)
        {
            byte[] data = new byte[dtls.GetReceiveLimit()];
            int received;
            try
            {
                received = dtls.Receive(data, 0, data.Length, 1);
            }
            catch (TlsFatalAlert alert)
            {
                Logger.Debug($"DTLS alert from {peer.EndPoint}: {alert.Message}");
                continue;
            }

            if (received > 0)
            {
                _listener.OnData(peer.Client, data, received);
            }
        }
    }

    private static void CloseSession(Peer peer)
    {
        try
        {
            peer.Client.DtlsTransport.Close();
        }
        catch (Exception e)
        {
            // Closing sends a final alert; a peer that is already gone cannot receive it, which changes nothing.
            Logger.Debug($"Could not close the session of {peer.EndPoint} cleanly: {e.Message}");
        }

        peer.Transport.Close();
    }

    private void Remove(Peer peer)
    {
        if (_peers.TryGetValue(peer.EndPoint, out Peer registered) && registered == peer)
        {
            _peers.Remove(peer.EndPoint);
        }
    }

    private static byte[] Copy(byte[] buffer, int length)
    {
        byte[] copy = new byte[length];
        Buffer.BlockCopy(buffer, 0, copy, 0, length);
        return copy;
    }
}
