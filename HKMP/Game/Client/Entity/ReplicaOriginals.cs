using System.Collections.Generic;
using UnityEngine;
using ActivateAllChildrenHook = On.HutongGames.PlayMaker.Actions.ActivateAllChildren;
using ActivateGameObjectHook = On.HutongGames.PlayMaker.Actions.ActivateGameObject;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// The originals kept switched off because another player hosts their room, where the replica is shown instead.
/// </summary>
/// <remarks>
/// The room's own state machines still run on this client and can switch such an original on, as an arena does
/// when it releases a wave of enemies. The entity's check each frame switches it off again, but only after that
/// frame is drawn, so the original shows for a moment before the replica appears. An activation by the room's
/// actions is undone as soon as the action runs, before anything is drawn.
/// </remarks>
internal static class ReplicaOriginals {
    private static readonly HashSet<GameObject> Objects = new HashSet<GameObject>();

    public static void Add(GameObject original) {
        if (original != null) {
            Objects.Add(original);
        }
    }

    public static void Remove(GameObject original) {
        if (original != null) {
            Objects.Remove(original);
        }
    }

    public static void RegisterHooks() {
        ActivateGameObjectHook.DoActivateGameObject += OnDoActivateGameObject;
        ActivateAllChildrenHook.OnEnter += OnActivateAllChildren;
    }

    public static void DeregisterHooks() {
        ActivateGameObjectHook.DoActivateGameObject -= OnDoActivateGameObject;
        ActivateAllChildrenHook.OnEnter -= OnActivateAllChildren;
        Objects.Clear();
    }

    private static void OnDoActivateGameObject(
        ActivateGameObjectHook.orig_DoActivateGameObject orig,
        HutongGames.PlayMaker.Actions.ActivateGameObject self
    ) {
        orig(self);
        if (self.activate.Value) {
            SwitchOffWithin(self.Fsm.GetOwnerDefaultTarget(self.gameObject));
        }
    }

    private static void OnActivateAllChildren(
        ActivateAllChildrenHook.orig_OnEnter orig,
        HutongGames.PlayMaker.Actions.ActivateAllChildren self
    ) {
        orig(self);
        if (self.activate) {
            SwitchOffWithin(self.gameObject.Value);
        }
    }

    /// <summary>
    /// Switches off the kept-off originals the activation reached: the object itself and anything beneath it,
    /// since switching a parent on also shows its children.
    /// </summary>
    private static void SwitchOffWithin(GameObject activated) {
        if (activated == null || Objects.Count == 0) {
            return;
        }

        foreach (var transform in activated.GetComponentsInChildren<Transform>(true)) {
            var obj = transform.gameObject;
            if (obj.activeSelf && Objects.Contains(obj)) {
                obj.SetActive(false);
            }
        }
    }
}
