using System.Collections.Generic;
using Hkmp.Networking.Packet.Data;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// The entities of the current room this client knows to be dead, and the life each entity is in: the deaths the
/// server recorded, received when entering the room and at every change of scene host, and the ones announced since.
/// </summary>
/// <remarks>
/// A new scene host used to decide from its own replica whether an entity was dead. The player that killed the entity
/// has no dead replica, and a replica tells nothing about deaths of an earlier visit, so dead entities were taken over
/// alive. The server's list decides instead. A dead entity only comes back in a later life, which the server accepts
/// only when the life is newer than the one in which the entity died.
/// </remarks>
internal sealed class KnownDeaths {
    private readonly HashSet<ushort> _dead = new HashSet<ushort>();
    private readonly Dictionary<ushort, ushort> _lives = new Dictionary<ushort, ushort>();

    /// <summary>Replaces what is known with the server's list, which already holds every death it recorded.</summary>
    public void Replace(IEnumerable<EntityLife> dead) {
        _dead.Clear();
        foreach (var death in dead) {
            _dead.Add(death.EntityId);
            Learn(death.EntityId, death.Life);
        }
    }

    /// <summary>Adds a death announced after the server's last list, in the life it happened.</summary>
    public void Add(ushort entityId, ushort life) {
        _dead.Add(entityId);
        Learn(entityId, life);
    }

    /// <summary>
    /// Takes in that the entity is alive in the given life, as the scene host reported. A life newer than the one in
    /// which the entity died brings it back.
    /// </summary>
    public void Alive(ushort entityId, ushort life) {
        if (_dead.Contains(entityId) && life > LifeOf(entityId)) {
            _dead.Remove(entityId);
        }

        Learn(entityId, life);
    }

    /// <summary>
    /// This client, as scene host, switched a dead entity on again, such as a pooled body that starts over. The
    /// entity starts its next life; an entity that is not known to be dead stays in the life it is in.
    /// </summary>
    /// <returns>The life the entity is in now.</returns>
    public ushort BeginLife(ushort entityId) {
        if (_dead.Remove(entityId)) {
            _lives[entityId] = (ushort) (LifeOf(entityId) + 1);
        }

        return LifeOf(entityId);
    }

    /// <summary>The life the entity is in, as far as this client knows.</summary>
    public ushort LifeOf(ushort entityId) => _lives.TryGetValue(entityId, out var life) ? life : (ushort) 0;

    /// <summary>Forgets the room, when this client leaves it.</summary>
    public void Clear() {
        _dead.Clear();
        _lives.Clear();
    }

    /// <summary>Whether the entity with the given ID is known to be dead.</summary>
    public bool Contains(ushort entityId) => _dead.Contains(entityId);

    private void Learn(ushort entityId, ushort life) {
        if (life > LifeOf(entityId)) {
            _lives[entityId] = life;
        }
    }
}
