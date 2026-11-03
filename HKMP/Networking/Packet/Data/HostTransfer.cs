namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data for a host transfer.
/// </summary>
internal class HostTransfer : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => true;
    
    /// <summary>
    /// The name of the scene in which the player becomes the scene host.
    /// </summary>
    public string SceneName { get; set; }

    /// <summary>
    /// Whether the player becomes the scene host, or stops being it because the role moved to another player.
    /// </summary>
    public bool IsHost { get; set; } = true;

    /// <summary>
    /// The position of this decision among the server's scene host decisions for this player.
    /// </summary>
    public uint RoleDecision { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write(IsHost);
        packet.Write(RoleDecision);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        IsHost = packet.ReadBool();
        RoleDecision = packet.ReadUInt();
    }
}
