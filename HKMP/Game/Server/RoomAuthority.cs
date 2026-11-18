using System.Collections.Generic;
using System.Linq;

namespace Hkmp.Game.Server;

/// <summary>
/// The server's authority over one room: which player hosts it in each of its epochs, and which of its entities died.
/// </summary>
/// <remarks>
/// Every copy of the game used to decide on its own whether an entity was dead, from what it happened to have: its
/// replica, data stored from an earlier visit, or nothing at all for the player that killed it. A new scene host then
/// took dead entities over alive. The deaths are kept here instead, and every copy follows this list.
/// <para>
/// An epoch is one stretch of the room under one scene host. It starts at one when the room gets its first host and
/// grows by one at every change of host, so a message tells which host sent it even when it arrives after a change.
/// </para>
/// </remarks>
internal sealed class RoomAuthority {
    private readonly object _lock = new object();
    private readonly Dictionary<uint, ushort> _hostOfEpoch = new Dictionary<uint, ushort>();
    private readonly HashSet<ushort> _dead = new HashSet<ushort>();

    /// <summary>The current epoch of the room, or zero while it has had no scene host.</summary>
    public uint Epoch {
        get {
            lock (_lock) {
                return _epoch;
            }
        }
    }

    private uint _epoch;

    /// <summary>
    /// Starts a new epoch hosted by the given player, when the room gets its first scene host or changes host.
    /// </summary>
    /// <param name="host">The ID of the player that now hosts the room.</param>
    /// <returns>The new epoch.</returns>
    public uint BeginHost(ushort host) {
        lock (_lock) {
            _epoch++;
            _hostOfEpoch[_epoch] = host;
            return _epoch;
        }
    }

    /// <summary>
    /// Records the death of an entity, reported by the player that hosted the room in the given epoch.
    /// </summary>
    /// <remarks>
    /// A death is a fact, not a state: one reported by a previous host is recorded even when it arrives after the
    /// room changed hands, because that host ran the entity when it died. Only a player that never hosted the room
    /// in that epoch is refused, such as one whose replica took a local hit.
    /// </remarks>
    /// <param name="sender">The ID of the player that reported the death.</param>
    /// <param name="epoch">The epoch in which the sender ran the entity.</param>
    /// <param name="entityId">The ID of the entity in the room.</param>
    /// <returns>Whether the death was recorded now; false when refused or already recorded.</returns>
    public bool RecordDeath(ushort sender, uint epoch, ushort entityId) {
        lock (_lock) {
            if (!_hostOfEpoch.TryGetValue(epoch, out var host) || host != sender) {
                return false;
            }

            return _dead.Add(entityId);
        }
    }

    /// <summary>
    /// Forgets the death of an entity that starts another life, such as a pooled body switched on again with the
    /// same ID.
    /// </summary>
    /// <param name="entityId">The ID of the entity in the room.</param>
    public void Revive(ushort entityId) {
        lock (_lock) {
            _dead.Remove(entityId);
        }
    }

    /// <summary>Whether the given player hosted the room in the given epoch.</summary>
    public bool HostedIn(ushort player, uint epoch) {
        lock (_lock) {
            return _hostOfEpoch.TryGetValue(epoch, out var host) && host == player;
        }
    }

    /// <summary>Whether the entity with the given ID is recorded as dead.</summary>
    public bool IsDead(ushort entityId) {
        lock (_lock) {
            return _dead.Contains(entityId);
        }
    }

    /// <summary>The IDs of the entities recorded as dead, as a copy safe to send.</summary>
    public List<ushort> DeadEntities() {
        lock (_lock) {
            return _dead.OrderBy(id => id).ToList();
        }
    }
}
