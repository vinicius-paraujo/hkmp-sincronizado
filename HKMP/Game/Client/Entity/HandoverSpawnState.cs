using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Brings the original object up to the replica's visible state when this client becomes its scene host.
/// </summary>
/// <remarks>
/// Many enemies start hidden or without a body and switch their collider and renderer on in a spawn state.
/// An original that stayed off while the replica fought never ran that state, so it would take over invisible or
/// without a collider and, being dynamic, fall through the floor. The replica follows the previous host, so its
/// state is the one to keep.
/// </remarks>
internal static class HandoverSpawnState {
    /// <summary>
    /// Copies the enabled state of the root collider and mesh renderer from the replica to the original.
    /// </summary>
    /// <param name="original">The original object this client now runs.</param>
    /// <param name="replica">The replica that showed the previous host's state.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public static void Apply(GameObject original, GameObject replica, string entity) {
        Copy(original.GetComponent<Collider2D>(), replica.GetComponent<Collider2D>(), entity, "Collider");
        Copy(original.GetComponent<MeshRenderer>(), replica.GetComponent<MeshRenderer>(), entity, "Renderer");
    }

    /// <summary>
    /// Switches on the parents that keep a shown original hidden.
    /// </summary>
    /// <remarks>
    /// Arenas keep their waves under groups that only the scene host switches on. On a client that watched the
    /// wave through replicas, the group is still off, so the original would take over hidden inside it.
    /// </remarks>
    /// <param name="original">The original object this client now runs, already set active.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public static void ShowParents(GameObject original, string entity) {
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

    private static void Copy(Collider2D original, Collider2D replica, string entity, string part) {
        if (original == null || replica == null || original.enabled == replica.enabled) return;
        Logger.Info($"  {part} of entity {entity} taken from the replica: {replica.enabled}");
        original.enabled = replica.enabled;
    }

    private static void Copy(Renderer original, Renderer replica, string entity, string part) {
        if (original == null || replica == null || original.enabled == replica.enabled) return;
        Logger.Info($"  {part} of entity {entity} taken from the replica: {replica.enabled}");
        original.enabled = replica.enabled;
    }
}
