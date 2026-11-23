using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// The datagram transport of one peer. The network thread delivers the peer's datagrams to it, and DTLS reads
/// them from it: on a handshake thread while the handshake runs, and on the network thread once it is
/// established. Sends go straight to the shared socket, addressed to the peer.
/// </summary>
internal sealed class PeerTransport : DatagramTransport
{
    /// <summary>
    /// The largest datagram DTLS may send or receive, below the usual path MTU.
    /// </summary>
    public const int MaxPacketSize = 1400;

    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly SemaphoreSlim _available = new(0);
    private volatile bool _established;
    private volatile bool _closed;

    /// <summary>
    /// Creates the transport of the peer at the given endpoint.
    /// </summary>
    /// <param name="socket">The server socket shared by all peers.</param>
    /// <param name="remote">The endpoint of the peer.</param>
    public PeerTransport(Socket socket, IPEndPoint remote)
    {
        _socket = socket;
        _remote = remote;
    }

    /// <summary>
    /// Whether delivered datagrams are waiting to be read.
    /// </summary>
    public bool HasPending => !_inbox.IsEmpty;

    /// <summary>
    /// Hands a datagram of the peer to the transport.
    /// </summary>
    /// <param name="datagram">The datagram, which the transport keeps.</param>
    public void Deliver(byte[] datagram)
    {
        if (_closed)
        {
            return;
        }

        _inbox.Enqueue(datagram);
        _available.Release();
    }

    /// <summary>
    /// Switches reading to never wait. The network thread reads only right after delivering a datagram, and
    /// waiting there would stall every other peer.
    /// </summary>
    public void MarkEstablished()
    {
        _established = true;
    }

    /// <inheritdoc />
    public int GetReceiveLimit()
    {
        return MaxPacketSize;
    }

    /// <inheritdoc />
    public int GetSendLimit()
    {
        return MaxPacketSize;
    }

    /// <inheritdoc />
    public int Receive(byte[] buf, int off, int len, int waitMillis)
    {
        if (_closed)
        {
            // A handshake waiting on a closed transport must end now, not when its deadline expires.
            throw new TlsFatalAlert(AlertDescription.user_canceled);
        }

        int wait = _established ? 0 : waitMillis;
        if (!_available.Wait(wait) || !_inbox.TryDequeue(out byte[] datagram))
        {
            return -1;
        }

        int length = System.Math.Min(len, datagram.Length);
        Buffer.BlockCopy(datagram, 0, buf, off, length);
        return length;
    }

    /// <inheritdoc />
    public void Send(byte[] buf, int off, int len)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            _socket.SendTo(buf, off, len, SocketFlags.None, _remote);
        }
        catch (Exception e) when (e is SocketException || e is ObjectDisposedException)
        {
            // A lost datagram is part of UDP; DTLS retransmits what matters.
            Logger.Debug($"Could not send to {_remote}: {e.Message}");
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        _closed = true;

        // Wakes a handshake that waits for data, so that it sees the transport closed.
        _available.Release();
    }
}
