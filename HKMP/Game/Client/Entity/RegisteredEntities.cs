using System.Collections.Generic;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// The objects that belong to an entity right now: the original and the replica of every registered entity,
/// children of other entities included.
/// </summary>
/// <remarks>
/// The registry file only says which objects can become entities, and its lookup sees the top-level entries alone.
/// A child declared under another entry, such as Hornet's needle, is an entity all the same, and must be treated as
/// one: its actions are not sent as plain actions, its parent's body does not count it as an own child, and it is
/// not carried by its parent.
/// </remarks>
internal static class RegisteredEntities {
    private static readonly HashSet<GameObject> Objects = new HashSet<GameObject>();

    public static void Add(GameObject gameObject) {
        if (gameObject != null) {
            Objects.Add(gameObject);
        }
    }

    public static void Remove(GameObject gameObject) {
        if (gameObject != null) {
            Objects.Remove(gameObject);
        }
    }

    /// <summary>
    /// Whether the object is an entity: registered now, or matching a top-level entry of the registry, as an object
    /// spawned from a pool is before its registration completes.
    /// </summary>
    public static bool IsEntity(GameObject gameObject) =>
        gameObject != null && (Objects.Contains(gameObject) || EntityRegistry.TryGetEntry(gameObject, out _));
}
