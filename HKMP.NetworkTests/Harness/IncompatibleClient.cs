using System;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// A DTLS client whose handshake the server must refuse: it offers only a cipher suite the server does not
/// support, as an outdated or foreign client would. The server ends that handshake with a fatal alert.
/// </summary>
internal sealed class IncompatibleClient : DefaultTlsClient {
    private IncompatibleClient() : base(new BcTlsCrypto()) {
    }

    /// <summary>
    /// Runs the handshake against the port and returns how it ended; null means it was accepted.
    /// </summary>
    public static Exception Handshake(int port) {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        try {
            new DtlsClientProtocol().Connect(new IncompatibleClient(), new UdpTransport(socket));
            return null;
        } catch (Exception e) {
            return e;
        }
    }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    protected override int[] GetSupportedCipherSuites() => new[] { CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 };

    public override int GetHandshakeTimeoutMillis() => 5000;

    public override TlsAuthentication GetAuthentication() => new AcceptAnyServer();

    private sealed class AcceptAnyServer : TlsAuthentication {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate) {
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => null;
    }

    private sealed class UdpTransport : DatagramTransport {
        private readonly Socket _socket;

        public UdpTransport(Socket socket) {
            _socket = socket;
        }

        public int GetReceiveLimit() => 1400;

        public int GetSendLimit() => 1400;

        public int Receive(byte[] buf, int off, int len, int waitMillis) {
            _socket.ReceiveTimeout = System.Math.Max(1, waitMillis);
            try {
                return _socket.Receive(buf, off, len, SocketFlags.None);
            } catch (SocketException) {
                return -1;
            }
        }

        public void Send(byte[] buf, int off, int len) => _socket.Send(buf, off, len, SocketFlags.None);

        public void Close() {
        }
    }
}
