using System.Collections.Generic;
using System.Linq;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Game.Server.Rooms;

/// <summary>
/// Why the scene host of a room changes.
/// </summary>
internal enum HostChangeReason
{
    /// <summary>The host left the room for another one.</summary>
    LeftRoom,

    /// <summary>The host stopped sending updates.</summary>
    Silent,

    /// <summary>The host left the server.</summary>
    Disconnected,
}

/// <summary>
/// A change of the scene host of a room, which the server announces to the new host and to the previous one.
/// </summary>
/// <param name="Scene">The scene of the room.</param>
/// <param name="PreviousHost">The player that hosted the room until now.</param>
/// <param name="NewHost">The player that hosts the room from now on.</param>
/// <param name="Epoch">The epoch that the new host starts.</param>
/// <param name="Dead">The entities of the room recorded as dead, with the life in which each died.</param>
internal sealed record HostChange(
    string Scene,
    ushort PreviousHost,
    ushort NewHost,
    uint Epoch,
    IReadOnlyList<EntityLife> Dead);

/// <summary>
/// What a player entering a room is told about it.
/// </summary>
/// <param name="IsHost">Whether the player hosts the room from now on.</param>
/// <param name="Epoch">The room's current epoch, or zero when no one hosts it.</param>
/// <param name="Dead">The entities of the room recorded as dead, with the life in which each died.</param>
internal sealed record RoomEntry(bool IsHost, uint Epoch, IReadOnlyList<EntityLife> Dead);

/// <summary>
/// The only owner of who hosts each room, of each room's epochs and of the deaths in it. Every change of host goes
/// through <see cref="Transfer"/>. It runs on the server core, so it needs no locks.
/// </summary>
/// <remarks>
/// An epoch is one stretch of a room under one host. Epochs come from a single counter of the server that only
/// grows, also across rooms that are forgotten and visited again, so a client orders every host decision it gets by
/// its epoch alone, and a message tells which host sent it even when it arrives after a change.
/// </remarks>
internal sealed class RoomHostAssignment
{
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly HostCandidatePolicy _policy;
    private uint _lastEpoch;

    /// <summary>
    /// Creates the assignment.
    /// </summary>
    /// <param name="policy">Chooses the host among the players of a room.</param>
    public RoomHostAssignment(HostCandidatePolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// The host of the room, or null when no one hosts it.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <returns>The ID of the host.</returns>
    public ushort? HostOf(string scene)
    {
        return _rooms.TryGetValue(scene, out Room room) ? room.Host : null;
    }

    /// <summary>
    /// Whether the player hosts the room now.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="player">The ID of the player.</param>
    /// <returns>True for the current host.</returns>
    public bool IsHost(string scene, ushort player)
    {
        return HostOf(scene) == player;
    }

    /// <summary>
    /// The current epoch of the room, or zero when it never had a host.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <returns>The epoch.</returns>
    public uint EpochOf(string scene)
    {
        return _rooms.TryGetValue(scene, out Room room) ? room.Epoch : 0;
    }

    /// <summary>
    /// A player enters a room. The first player of a room without a host becomes its host.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="player">The ID of the player.</param>
    /// <param name="hostsRooms">Whether rooms have hosts at all, which only full synchronisation uses.</param>
    /// <returns>What the player is told about the room.</returns>
    public RoomEntry Enter(string scene, ushort player, bool hostsRooms)
    {
        Room room = RoomOf(scene);
        if (hostsRooms && room.Host == null)
        {
            BeginEpoch(room, player);
            return new RoomEntry(true, room.Epoch, room.DeadEntities());
        }

        return new RoomEntry(false, room.Epoch, room.DeadEntities());
    }

    /// <summary>
    /// Moves the room away from its host, to the candidate the policy chooses. Nothing changes when the player does
    /// not host the room, or when there is no candidate; a host that left the room or the server leaves the room
    /// without a host then.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="host">The ID of the player that hosts the room now.</param>
    /// <param name="candidates">The other players in the room.</param>
    /// <param name="reason">Why the host changes.</param>
    /// <returns>The change to announce, or null when no one takes the room over.</returns>
    public HostChange Transfer(string scene, ushort host, IEnumerable<ushort> candidates, HostChangeReason reason)
    {
        if (!_rooms.TryGetValue(scene, out Room room) || room.Host != host)
        {
            return null;
        }

        ushort? next = _policy.Choose(candidates.Where(player => player != host));
        if (next == null)
        {
            // A silent host keeps the room, since no one could run it instead; a host that is gone cannot.
            if (reason != HostChangeReason.Silent)
            {
                room.Host = null;
            }

            return null;
        }

        BeginEpoch(room, next.Value);
        return new HostChange(scene, host, next.Value, room.Epoch, room.DeadEntities());
    }

    /// <summary>
    /// Records the death of an entity, reported by the player that hosted the room in the given epoch.
    /// </summary>
    /// <remarks>
    /// A death is a fact, not a state: one reported by a previous host is recorded even after the room changed
    /// hands, because that host ran the entity when it died. A player that never hosted the room in that epoch is
    /// refused, such as one whose replica took a local hit.
    /// </remarks>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="sender">The ID of the player that reported the death.</param>
    /// <param name="epoch">The epoch in which the sender ran the entity.</param>
    /// <param name="death">The entity and the life in which it died.</param>
    /// <returns>Whether the death was recorded now; false when refused or already recorded.</returns>
    public bool RecordDeath(string scene, ushort sender, uint epoch, EntityLife death)
    {
        Room room = RoomOf(scene);
        if (!room.HostedIn(sender, epoch))
        {
            return false;
        }

        if (room.Dead.TryGetValue(death.EntityId, out ushort life) && life >= death.Life)
        {
            return false;
        }

        room.Dead[death.EntityId] = death.Life;
        return true;
    }

    /// <summary>
    /// Brings a dead entity back, which only a later life does. A host that reports the entity active in the life
    /// in which it died, such as one that took the room over before it heard of the death, changes nothing.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="life">The entity and the life it is in now.</param>
    /// <returns>True when the entity is alive in that life; false when it stays dead.</returns>
    public bool Revive(string scene, EntityLife life)
    {
        Room room = RoomOf(scene);
        if (!room.Dead.TryGetValue(life.EntityId, out ushort deathLife))
        {
            return true;
        }

        if (life.Life <= deathLife)
        {
            return false;
        }

        room.Dead.Remove(life.EntityId);
        return true;
    }

    /// <summary>
    /// Whether the player hosted the room in the given epoch.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="player">The ID of the player.</param>
    /// <param name="epoch">The epoch.</param>
    /// <returns>True when that player started that epoch.</returns>
    public bool HostedIn(string scene, ushort player, uint epoch)
    {
        return _rooms.TryGetValue(scene, out Room room) && room.HostedIn(player, epoch);
    }

    /// <summary>
    /// Whether the entity is recorded as dead in the given life or a later one.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <param name="death">The entity and the life.</param>
    /// <returns>True when that death is recorded.</returns>
    public bool IsDead(string scene, EntityLife death)
    {
        return _rooms.TryGetValue(scene, out Room room)
            && room.Dead.TryGetValue(death.EntityId, out ushort life)
            && life >= death.Life;
    }

    /// <summary>
    /// The entities of the room recorded as dead.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    /// <returns>A copy, safe to send.</returns>
    public IReadOnlyList<EntityLife> DeadEntities(string scene)
    {
        return _rooms.TryGetValue(scene, out Room room) ? room.DeadEntities() : [];
    }

    /// <summary>
    /// Forgets a room once its last player left. The game loads the room again on the next visit, with every entity
    /// alive, so nothing recorded about this visit applies; the epoch counter keeps growing all the same.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    public void Forget(string scene)
    {
        _rooms.Remove(scene);
    }

    /// <summary>
    /// Forgets every room, when the server stops.
    /// </summary>
    public void Clear()
    {
        _rooms.Clear();
    }

    private Room RoomOf(string scene)
    {
        if (!_rooms.TryGetValue(scene, out Room room))
        {
            room = new Room();
            _rooms[scene] = room;
        }

        return room;
    }

    private void BeginEpoch(Room room, ushort host)
    {
        _lastEpoch++;
        room.Epoch = _lastEpoch;
        room.Host = host;
        room.HostOfEpoch[_lastEpoch] = host;
    }

    /// <summary>
    /// What the server knows of one room.
    /// </summary>
    private sealed class Room
    {
        public ushort? Host { get; set; }

        public uint Epoch { get; set; }

        public Dictionary<uint, ushort> HostOfEpoch { get; } = new();

        public Dictionary<ushort, ushort> Dead { get; } = new();

        public bool HostedIn(ushort player, uint epoch)
        {
            return HostOfEpoch.TryGetValue(epoch, out ushort host) && host == player;
        }

        public IReadOnlyList<EntityLife> DeadEntities()
        {
            return Dead.OrderBy(pair => pair.Key).Select(pair => new EntityLife(pair.Key, pair.Value)).ToList();
        }
    }
}
