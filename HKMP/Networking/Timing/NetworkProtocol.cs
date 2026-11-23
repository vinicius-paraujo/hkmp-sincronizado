namespace Hkmp.Networking;

/// <summary>
/// The version of the network protocol. A client and a server only talk when both speak the same one, and the
/// server tells a client of another version why it was refused.
/// </summary>
internal static class NetworkProtocol
{
    /// <summary>
    /// The current version. It grows whenever the admission or the session change in a way the other side would
    /// misread.
    /// </summary>
    public const ushort Version = 2;
}
