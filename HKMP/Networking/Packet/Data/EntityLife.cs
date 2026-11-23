using System.Collections.Generic;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// An entity of a room and one of its lives. An entity that dies stays dead until it starts a later life, such as a
/// pooled body switched on again with the same ID.
/// </summary>
/// <param name="EntityId">The ID of the entity in the room.</param>
/// <param name="Life">The life, counted from zero.</param>
internal readonly record struct EntityLife(ushort EntityId, ushort Life);

/// <summary>
/// Writes and reads a list of entity lives, such as the entities of a room the server recorded as dead.
/// </summary>
internal static class EntityLifeList {
    public static void Write(IPacket packet, IReadOnlyList<EntityLife> lives) {
        var count = (ushort) System.Math.Min(lives.Count, ushort.MaxValue);
        packet.Write(count);
        for (var i = 0; i < count; i++) {
            packet.Write(lives[i].EntityId);
            packet.Write(lives[i].Life);
        }
    }

    public static List<EntityLife> Read(IPacket packet) {
        var count = packet.ReadUShort();
        var lives = new List<EntityLife>(count);
        for (var i = 0; i < count; i++) {
            var id = packet.ReadUShort();
            lives.Add(new EntityLife(id, packet.ReadUShort()));
        }

        return lives;
    }
}
