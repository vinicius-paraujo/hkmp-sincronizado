namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Reads the plain header of a DTLS 1.2 record, which is enough to tell whether a datagram starts a handshake.
/// </summary>
internal static class DtlsRecord
{
    private const byte HandshakeContentType = 22;
    private const byte ClientHelloMessageType = 1;

    // Content type (1), version (2), epoch (2), sequence number (6) and length (2).
    private const int RecordHeaderLength = 13;
    private const int EpochOffset = 3;

    /// <summary>
    /// Whether the datagram begins with an unencrypted client hello. Only that record may create state for an
    /// unknown endpoint; everything else from such an endpoint is dropped.
    /// </summary>
    /// <param name="buffer">The datagram.</param>
    /// <param name="length">The number of bytes received.</param>
    /// <returns>True if the first record is a client hello in epoch zero.</returns>
    public static bool IsClientHello(byte[] buffer, int length)
    {
        return length > RecordHeaderLength
            && buffer[0] == HandshakeContentType
            && buffer[EpochOffset] == 0
            && buffer[EpochOffset + 1] == 0
            && buffer[RecordHeaderLength] == ClientHelloMessageType;
    }
}
