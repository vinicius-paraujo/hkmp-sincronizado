using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Hkmp.Logging;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Checks the DTLS cookie of a client hello without keeping any state. A client hello without a valid cookie is
/// answered with a hello verify request, so only a client that can receive at its claimed address gets a peer.
/// </summary>
internal sealed class CookieGate
{
    private readonly DtlsVerifier _verifier;
    private readonly Socket _socket;

    /// <summary>
    /// Creates the gate.
    /// </summary>
    /// <param name="crypto">The crypto that signs the cookies.</param>
    /// <param name="socket">The server socket, used to answer with hello verify requests.</param>
    public CookieGate(TlsCrypto crypto, Socket socket)
    {
        _verifier = new DtlsVerifier(crypto);
        _socket = socket;
    }

    /// <summary>
    /// Verifies a datagram from an endpoint.
    /// </summary>
    /// <param name="from">The endpoint the datagram came from.</param>
    /// <param name="buffer">The datagram.</param>
    /// <param name="length">The number of bytes received.</param>
    /// <returns>The request that starts a handshake, or null if the datagram does not start one.</returns>
    public DtlsRequest Verify(IPEndPoint from, byte[] buffer, int length)
    {
        if (!DtlsRecord.IsClientHello(buffer, length))
        {
            return null;
        }

        try
        {
            return _verifier.VerifyRequest(ClientId(from), buffer, 0, length, new Reply(_socket, from));
        }
        catch (IOException e)
        {
            Logger.Debug($"Malformed client hello from {from}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The bytes the cookie is bound to: the address and the port of the endpoint.
    /// </summary>
    private static byte[] ClientId(IPEndPoint endPoint)
    {
        byte[] address = endPoint.Address.GetAddressBytes();
        byte[] id = new byte[address.Length + 2];
        Buffer.BlockCopy(address, 0, id, 0, address.Length);
        id[address.Length] = (byte) (endPoint.Port >> 8);
        id[address.Length + 1] = (byte) endPoint.Port;
        return id;
    }

    /// <summary>
    /// Sends the hello verify request back to the endpoint that sent the client hello.
    /// </summary>
    private sealed class Reply : DatagramSender
    {
        private readonly Socket _socket;
        private readonly IPEndPoint _to;

        public Reply(Socket socket, IPEndPoint to)
        {
            _socket = socket;
            _to = to;
        }

        public int GetSendLimit()
        {
            return PeerTransport.MaxPacketSize;
        }

        public void Send(byte[] buf, int off, int len)
        {
            try
            {
                _socket.SendTo(buf, off, len, SocketFlags.None, _to);
            }
            catch (Exception e) when (e is SocketException || e is ObjectDisposedException)
            {
                Logger.Debug($"Could not answer the client hello of {_to}: {e.Message}");
            }
        }
    }
}
