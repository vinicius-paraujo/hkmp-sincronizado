using System.Collections.Generic;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// The entities of the current room this client knows to be dead: the ones the server recorded, received when entering
/// the room and at every change of scene host, and the deaths announced since.
/// </summary>
/// <remarks>
/// A new scene host used to decide from its own replica whether an entity was dead. The player that killed the entity
/// has no dead replica, and a replica tells nothing about deaths of an earlier visit, so dead entities were taken over
/// alive. The server's list decides instead.
/// </remarks>
internal sealed class KnownDeaths {
    private readonly HashSet<ushort> _dead = new HashSet<ushort>();

    /// <summary>Replaces what is known with the server's list, which already holds every death it recorded.</summary>
    public void Replace(IEnumerable<ushort> dead) {
        _dead.Clear();
        _dead.UnionWith(dead);
    }

    /// <summary>Adds a death announced after the server's last list.</summary>
    public void Add(ushort entityId) => _dead.Add(entityId);

    /// <summary>Forgets the room, when this client leaves it.</summary>
    public void Clear() => _dead.Clear();

    /// <summary>Whether the entity with the given ID is known to be dead.</summary>
    public bool Contains(ushort entityId) => _dead.Contains(entityId);
}
