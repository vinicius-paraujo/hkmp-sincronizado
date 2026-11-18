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
/// Ends a Godhome fight on players that are not the scene host when the replica of one of its bosses dies.
/// </summary>
internal static class GodhomeBossDeath {
    /// <summary>
    /// Checks whether this is an enemy in a godhome fight. If that's the case, the health manager of the client
    /// object will have their death be registered as a trigger for the boss scene controller. This ensures that
    /// fights will end on scene clients if the client objects die.
    /// </summary>
    public static void Register(GameObject original, GameObject replica, string entity) {
        var bossSceneControllers = UnityEngine.Object.FindObjectsOfType<BossSceneController>();
        var bossSceneController = bossSceneControllers.FirstOrDefault(
            con => con.gameObject.scene.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene())
        );
        if (bossSceneController == null) {
            return;
        }
        
        var hostHealthManager = original.GetComponent<HealthManager>();
        if (hostHealthManager == null) {
            return;
        }
        
        var clientHealthManager = replica.GetComponent<HealthManager>();
        if (clientHealthManager == null) {
            Logger.Debug($"Entity {entity} has HealthManager on host but not on client");
            return;
        }
        
        if (!bossSceneController.bosses.Contains(hostHealthManager)) {
            return;
        }
        
        Logger.Debug($"Entity {entity} is contained in the boss scene controller, registering on death");
        
        clientHealthManager.OnDeath += () => {
            Logger.Debug("OnDeath triggered for health manager in boss scene controller");
            
            var bossesLeft = ReflectionHelper.GetField<BossSceneController, int>(bossSceneController, "bossesLeft");
            ReflectionHelper.SetField(bossSceneController, "bossesLeft", bossesLeft - 1);
            
            ReflectionHelper.CallMethod(bossSceneController, "CheckBossesDead");
        };
    }
}
