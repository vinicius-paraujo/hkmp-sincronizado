using HutongGames.PlayMaker;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Fills the object references of a machine that is resumed in the middle of a fight.
/// </summary>
/// <remarks>
/// References to objects cannot be sent between players, and the states that store them, such as the owner or
/// the hero, usually run once at the start. A machine resumed mid-fight would keep them empty and stop at the
/// first action that needs them, or fall back to its idle state.
/// </remarks>
internal static class HandoverFsmReferences {
    /// <summary>
    /// Sets each empty reference to the owner or to the local knight, according to its name.
    /// </summary>
    /// <param name="fsm">The resumed machine.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public static void Fill(PlayMakerFSM fsm, string entity) {
        // A machine that finds one of its own children, such as a summon finding the enemy it carries, does so once
        // and keeps it; the search is run again for every such reference that is still empty.
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action is HutongGames.PlayMaker.Actions.FindChild find && find.storeResult != null
                    && find.storeResult.Value == null) {
                    var owner = fsm.Fsm.GetOwnerDefaultTarget(find.gameObject);
                    var child = owner == null ? null : owner.transform.Find(find.childName.Value);
                    if (child == null) continue;
                    find.storeResult.Value = child.gameObject;
                    Logger.Info($"  Reference '{find.storeResult.Name}' of {fsm.FsmName} on entity {entity} found again: {child.name}");
                }
            }
        }

        var hero = HeroController.instance == null ? null : HeroController.instance.gameObject;
        foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
            if (variable.Value != null) continue;
            var kind = FsmReferenceName.Of(variable.Name);
            var value = kind == FsmReferenceName.Kind.Self ? fsm.gameObject : kind == FsmReferenceName.Kind.Hero ? hero : null;
            if (value == null) continue;
            variable.Value = value;
            Logger.Info($"  Reference '{variable.Name}' of {fsm.FsmName} on entity {entity} set to {value.name}");
        }
    }
}
