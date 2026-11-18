using System.Collections.Generic;

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

    /// <summary>
    /// The room's epoch the new scene host starts, which it stamps on what it sends about the room's entities.
    /// </summary>
    public uint Epoch { get; set; }

    /// <summary>
    /// The entities of the room the server recorded as dead, which the new scene host takes over dead.
    /// </summary>
    public List<ushort> DeadEntities { get; set; } = new List<ushort>();

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write(IsHost);
        packet.Write(RoleDecision);
        packet.Write(Epoch);
        EntityIdList.Write(packet, DeadEntities);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        IsHost = packet.ReadBool();
        RoleDecision = packet.ReadUInt();
        Epoch = packet.ReadUInt();
        DeadEntities = EntityIdList.Read(packet);
    }
}
