namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// The round trip time of one player's connection as the server measures it, so every player can see everyone's.
/// A newer report replaces an older one, so it is sent unreliably.
/// </summary>
internal class PlayerPing : GenericClientData {
    /// <summary>
    /// The round trip time in milliseconds.
    /// </summary>
    public ushort Rtt { get; set; }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        base.WriteData(packet);
        packet.Write(Rtt);
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        base.ReadData(packet);
        Rtt = packet.ReadUShort();
    }
}
