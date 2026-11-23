namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// Receives what happens to established peers. Every call comes from the network thread.
/// </summary>
internal interface IPeerListener
{
    /// <summary>
    /// A handshake succeeded, and the client can now exchange data.
    /// </summary>
    /// <param name="client">The client of the new peer.</param>
    public void OnEstablished(DtlsServerClient client);

    /// <summary>
    /// Decrypted data arrived from a client.
    /// </summary>
    /// <param name="client">The client that sent the data.</param>
    /// <param name="buffer">The data, in a buffer that belongs to the listener from now on.</param>
    /// <param name="length">The number of bytes of data in the buffer.</param>
    public void OnData(DtlsServerClient client, byte[] buffer, int length);

    /// <summary>
    /// An established peer ended, because the server closed it or because a new handshake replaced it.
    /// </summary>
    /// <param name="client">The client of the peer that ended.</param>
    public void OnClosed(DtlsServerClient client);
}
