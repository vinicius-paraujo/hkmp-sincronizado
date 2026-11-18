using Hkmp.Game.Client.Entity;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// The death of an entity, as the scene host reports it in the entity's <c>Death</c> data: what the replicas need to
/// die the same way, and the entity's type, which the server needs to tell addons which entity died.
/// </summary>
/// <remarks>
/// The client writes and reads it, and the server reads it to record the death, so the format lives in one place.
/// </remarks>
internal sealed class EntityDeathData {
    /// <summary>The type of the entity that died.</summary>
    public EntityType EntityType { get; set; }

    /// <summary>
    /// The name of the entity's object in the room, such as "Giant Fly", which addons match against their own data.
    /// </summary>
    public string EntityName { get; set; } = "";

    /// <summary>The direction of the killing hit, when it had one.</summary>
    public float? AttackDirection { get; set; }

    /// <summary>The game's attack type of the killing hit, as a byte.</summary>
    public byte AttackType { get; set; }

    /// <summary>Whether the killing hit ignored evasion.</summary>
    public bool IgnoreEvasion { get; set; }

    public void WriteData(IPacket packet) {
        packet.Write((ushort) EntityType);
        packet.Write(EntityName);
        packet.Write(AttackDirection.HasValue);
        if (AttackDirection.HasValue) {
            packet.Write(AttackDirection.Value);
        }

        packet.Write(AttackType);
        packet.Write(IgnoreEvasion);
    }

    public static EntityDeathData ReadData(IPacket packet) {
        var death = new EntityDeathData {
            EntityType = (EntityType) packet.ReadUShort(),
            EntityName = packet.ReadString()
        };
        if (packet.ReadBool()) {
            death.AttackDirection = packet.ReadFloat();
        }

        death.AttackType = packet.ReadByte();
        death.IgnoreEvasion = packet.ReadBool();
        return death;
    }
}
