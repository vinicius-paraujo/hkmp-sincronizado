using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// A child entity the game takes out of its parent, such as Hornet's needle, which her own machines move out of her
/// when she throws it and back in when she catches it.
/// </summary>
/// <remarks>
/// A child entity sends its position relative to its parent. Once the game took the original out of the parent, that
/// position is in the room's space, while the replica is still inside the parent's replica, so the replica was drawn
/// far from the room. Each copy therefore keeps the same hierarchy as the scene host's original, and the position is
/// sent in the space the original is in.
/// </remarks>
internal partial class Entity {
    /// <summary>The parent the original was declared under, or null for an entity without a parent.</summary>
    private Transform _declaredParent;

    /// <summary>The parent the replica was declared under, or null for an entity without a parent.</summary>
    private Transform _declaredReplicaParent;

    /// <summary>
    /// Whether the original is out of its declared parent: read from the original on the scene host, and from the
    /// last body state elsewhere.
    /// </summary>
    private bool _detached;

    /// <summary>Whether the position of this entity is sent and applied relative to its declared parent.</summary>
    private bool UsesParentSpace => _hasParent && !_detached;

    /// <summary>Remembers the declared parents of both objects, before the game can move either.</summary>
    private void RememberDeclaredParents() {
        if (!_hasParent) return;
        _declaredParent = Object.Host.transform.parent;
        _declaredReplicaParent = Object.Client.transform.parent;
    }

    /// <summary>Whether the game took the original out of its declared parent on this client.</summary>
    private bool OriginalIsDetached() => _hasParent && Object.Host.transform.parent != _declaredParent;

    /// <summary>
    /// Puts the given object in or out of its declared parent, as the scene host's original is, keeping where it is
    /// in the room.
    /// </summary>
    /// <param name="body">The original or the replica.</param>
    /// <param name="declaredParent">The parent that object was declared under.</param>
    /// <param name="detached">Whether the scene host's original is out of its parent.</param>
    private void MatchHierarchy(GameObject body, Transform declaredParent, bool detached) {
        if (!_hasParent || body == null) return;
        var target = detached ? null : declaredParent;
        if (body.transform.parent == target) return;

        body.transform.SetParent(target, true);
        Logger.Debug($"  Entity ({Id}, {Type}) '{body.name}' {(detached ? "taken out of" : "put back in")} its parent, as on the scene host");
    }
}
