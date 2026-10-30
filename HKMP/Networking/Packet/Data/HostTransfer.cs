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

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write(IsHost);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        IsHost = packet.ReadBool();
    }
}
