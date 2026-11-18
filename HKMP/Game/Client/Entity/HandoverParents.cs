using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Shows the original of an entity taken over inside a group that only the previous scene host switched on.
/// </summary>
internal static class HandoverParents {
    /// <summary>
    /// Switches on the parents that keep a shown original hidden.
    /// </summary>
    /// <remarks>
    /// Arenas keep their waves under groups that only the scene host switches on. On a client that watched the
    /// wave through replicas, the group is still off, so the original would take over hidden inside it.
    /// </remarks>
    /// <param name="original">The original object this client now runs, already set active.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public static void Show(GameObject original, string entity) {
        if (!original.activeSelf || original.activeInHierarchy) return;
        for (var parent = original.transform.parent; parent != null; parent = parent.parent) {
            if (parent.gameObject.activeSelf) continue;
            // A carrier such as a summon releases its enemy by taking it out of itself and switching itself off.
            // That release ran on the previous host only, so here the enemy is still inside the carrier; switching
            // the carrier back on would restart it with the enemy trapped, so the enemy is taken out instead.
            if (EntityRegistry.TryGetEntry(parent.gameObject, out _)) {
                Logger.Info($"  Entity {entity} taken out of '{parent.name}', which released it on the previous host");
                original.transform.SetParent(null, true);
                return;
            }
            Logger.Info($"  Parent '{parent.name}' of entity {entity} switched on, as the previous host had it");
            parent.gameObject.SetActive(true);
        }
    }
}
