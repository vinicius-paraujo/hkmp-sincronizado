using System.Collections.Generic;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Writes and reads a list of entity IDs, such as the entities of a room the server recorded as dead.
/// </summary>
internal static class EntityIdList {
    public static void Write(IPacket packet, List<ushort> ids) {
        var count = (ushort) System.Math.Min(ids.Count, ushort.MaxValue);
        packet.Write(count);
        for (var i = 0; i < count; i++) {
            packet.Write(ids[i]);
        }
    }

    public static List<ushort> Read(IPacket packet) {
        var count = packet.ReadUShort();
        var ids = new List<ushort>(count);
        for (var i = 0; i < count; i++) {
            ids.Add(packet.ReadUShort());
        }

        return ids;
    }
}
