using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

/// <summary>Preserves independently activated child objects when their owner changes scene host.</summary>
internal static class EntityChildState {
    public static void Restore(GameObject host, GameObject client) {
        // Registered children have their own handover. Pair the remaining children by name and occurrence,
        // since removing registered children from the replica changes sibling indices.
        var children = host.transform.Cast<Transform>()
            .Select(child => child.gameObject)
            .Where(child => !EntityRegistry.TryGetEntry(child, out _))
            .GroupBy(child => child.name)
            .ToDictionary(group => group.Key, group => new Queue<GameObject>(group));
        foreach (Transform child in client.transform) {
            if (!children.TryGetValue(child.name, out var matches) || matches.Count == 0) continue;
            var original = matches.Dequeue();
            // The replica root is already disabled; activeInHierarchy would turn every hitbox off.
            original.SetActive(child.gameObject.activeSelf);
            Restore(original, child.gameObject);
        }
    }
}
