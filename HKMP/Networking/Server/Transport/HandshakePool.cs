using System;
using System.Threading;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Runs server handshakes, each on its own thread and in its own peer's transport, so that one peer can never
/// disturb another. The number running at once is bounded; a client hello that finds the pool full is dropped,
/// and the client sends it again on its own DTLS timer.
/// </summary>
internal sealed class HandshakePool
{
    /// <summary>
    /// The largest number of handshakes that run at the same time.
    /// </summary>
    public const int Capacity = 4;

    private int _running;

    /// <summary>
    /// The number of handshakes running now.
    /// </summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>
    /// Starts the handshake of a peer if there is room.
    /// </summary>
    /// <param name="peer">The peer, in the handshake state.</param>
    /// <param name="request">The verified client hello that starts the handshake.</param>
    /// <param name="finished">Called on the handshake thread with the transport, or with the failure.</param>
    /// <returns>False if the pool is full and the handshake was not started.</returns>
    public bool TryBegin(Peer peer, DtlsRequest request, Action<Peer, DtlsTransport, Exception> finished)
    {
        if (Interlocked.Increment(ref _running) > Capacity)
        {
            Interlocked.Decrement(ref _running);
            return false;
        }

        Thread thread = new(() => Run(peer, request, finished))
        {
            IsBackground = true,
            Name = $"Handshake {peer.EndPoint}",
        };
        thread.Start();
        return true;
    }

    private void Run(Peer peer, DtlsRequest request, Action<Peer, DtlsTransport, Exception> finished)
    {
        DtlsTransport transport = null;
        Exception failure = null;
        try
        {
            transport = new DtlsServerProtocol().Accept(peer.TlsServer, peer.Transport, request);
        }
        catch (Exception e)
        {
            // Every failure of a handshake concerns only its own peer, so none of them may escape this thread.
            failure = e;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        finished(peer, transport, failure);
    }
}
