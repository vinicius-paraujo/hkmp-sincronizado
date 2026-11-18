using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Hkmp.Collection;
using Hkmp.Fsm;
using Hkmp.Game.Client.Entity.Action;
using Hkmp.Game.Client.Entity.Component;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using HutongGames.PlayMaker;
using Modding;
using On.HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;
using Vector2 = Hkmp.Math.Vector2;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Hooks into the game that keep the pair of objects of an entity apart: the replica out of the object pool, and
/// the original's activation recorded while the scene host is not known yet.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Callback method for when a game object is recycled. Used to prevent client objects from being recycled, which
    /// shouldn't happen because they are instantiated manually instead of from a pool.
    /// </summary>
    private void ObjectPoolOnRecycleGameObject(On.ObjectPool.orig_Recycle_GameObject orig, GameObject obj) {
        if (obj == Object.Client) {
            Logger.Debug($"Client object of entity: {Id}, {Type} tried to be recycled");
            return;
        }

        orig(obj);
    }
    
    /// <summary>
    /// Callback method for when the 'active' of the host game object is changed. Used to update whether the host
    /// game object should return to what active state after the scene host is determined.
    /// </summary>
    private void OnDoActivateGameObject(ActivateGameObject.orig_DoActivateGameObject orig, HutongGames.PlayMaker.Actions.ActivateGameObject self) {
        // If the game object in the action is not our host game object, we skip it
        if (self.Fsm.GetOwnerDefaultTarget(self.gameObject) != Object.Host || Object.Host == null) {
            orig(self);
            return;
        }
        
        // If the host client is determined already we skip (although this hook should have been deregistered
        if (_isSceneHostDetermined) {
            orig(self);
            return;
        }
        
        Logger.Debug($"Entity '{Object.Host.name}' tried changing active of host object, while host is not determined yet, updating original active to: {self.activate.Value}");

        // Update the original active value to whatever this action will set
        // Also, we do not let this action execute any further since we do not want it to modify our host object
        // before the scene host is determined
        _originalIsActive = self.activate.Value;
    }
}
