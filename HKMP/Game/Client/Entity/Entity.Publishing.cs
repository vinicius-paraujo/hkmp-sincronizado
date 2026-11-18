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
/// The scene host side of an entity: sends what the original does, every frame.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Sends what the original did this frame: its position, scale, presentation, activity and the changes of its
    /// machines.
    /// </summary>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private void PublishFrame() {
        var transform = Object.Host.transform;

        _detached = OriginalIsDetached();
        var newPosition = UsesParentSpace ? transform.localPosition : transform.position;
        if (newPosition != _lastPosition) {
            _lastPosition = newPosition;

            _netClient.UpdateManager.UpdateEntityPosition(
                Id,
                new Vector2(newPosition.x, newPosition.y)
            );
        }

        const float epsilon = 0.0001f;

        var newScale = _hasParent ? transform.localScale : transform.lossyScale;
        if (newScale != _lastScale) {
            var scaleData = new EntityUpdate.ScaleData {
                origin = true
            };

            if (newScale.x != _lastScale.x) {
                scaleData.x = true;
                scaleData.xScale = newScale.x;

                if (System.Math.Abs(newScale.x - _lastScale.x * -1) < epsilon) {
                    scaleData.xFlipped = true;
                }
            }
            
            if (newScale.y != _lastScale.y) {
                scaleData.y = true;
                scaleData.yScale = newScale.y;

                if (System.Math.Abs(newScale.y - _lastScale.y * -1) < epsilon) {
                    scaleData.yFlipped = true;
                }
            }
            
            if (newScale.z != _lastScale.z) {
                scaleData.z = true;
                scaleData.zScale = newScale.z;

                if (System.Math.Abs(newScale.z - _lastScale.z * -1) < epsilon) {
                    scaleData.zFlipped = true;
                }
            }

            _netClient.UpdateManager.UpdateEntityScale(Id, scaleData);
            
            _lastScale = newScale;
        }

        if (_hostScene.isLoaded) PublishBodyState();

        var newActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        // Likewise, a room being unloaded switches its objects off on the way out.
        if (newActive != _lastIsActive && (newActive || _hostScene.isLoaded)) {
            _lastIsActive = newActive;

            Logger.Info($"Entity '{Object.Host.name}' changed active: {newActive}");

            _netClient.UpdateManager.UpdateEntityIsActive(
                Id,
                newActive
            );
        }

        for (byte fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

            var data = new EntityHostFsmData();

            var lastStateName = snapshot.CurrentState;
            if (fsm.ActiveStateName != lastStateName) {
                snapshot.CurrentState = fsm.ActiveStateName;

                data.Types.Add(EntityHostFsmData.Type.State);
                data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);
                
                Logger.Debug($"Entity ({Id}, {Type}) host changed states: {lastStateName}, {fsm.ActiveStateName}");
            }

            // Define a method that allows generalization of checking for changes in all FSM variables
            void CondAddData<TVar, TBase, TData>(
                TVar[] fsmVars,
                TBase[] snapshotArray,
                Func<TVar, TBase> fsmVarValue,
                EntityHostFsmData.Type type,
                Dictionary<byte, TData> dataDict
            ) {
                for (byte i = 0; i < fsmVars.Length; i++) {
                    var fsmVar = fsmVars[i];
                    var snapshotVar = snapshotArray[i];

                    if (snapshotVar == null) {
                        Logger.Warn("No last value found for FSM var");
                        continue;
                    }

                    var value = fsmVarValue.Invoke(fsmVar);
                    if (!value.Equals(snapshotVar)) {
                        // Update the value in the snapshot since it changed
                        snapshotArray[i] = value;

                        data.Types.Add(type);
                        // Some funky casting here to make sure we can use this method with Vector2 and Vector3
                        // Since there is a mismatch between our Hkmp.Math.Vector2 and Unity's Vector2
                        // But our types have explicit converters, so casting is possible
                        if (value is UnityEngine.Vector2 vec2) {
                            dataDict[i] = (TData) (object) (Vector2) vec2;
                        } else if (value is Vector3 vec3) {
                            dataDict[i] = (TData) (object) (Hkmp.Math.Vector3) vec3;
                        } else {
                            dataDict[i] = (TData) (object) value;
                        }
                    }
                }
            }

            CondAddData(
                fsm.FsmVariables.FloatVariables, 
                snapshot.Floats,
                fsmFloat => fsmFloat.Value,
                EntityHostFsmData.Type.Floats,
                data.Floats
            );
            CondAddData(
                fsm.FsmVariables.IntVariables, 
                snapshot.Ints,
                fsmInt => fsmInt.Value,
                EntityHostFsmData.Type.Ints,
                data.Ints
            );
            CondAddData(
                fsm.FsmVariables.BoolVariables, 
                snapshot.Bools,
                fsmBool => fsmBool.Value,
                EntityHostFsmData.Type.Bools,
                data.Bools
            );
            CondAddData(
                fsm.FsmVariables.StringVariables, 
                snapshot.Strings,
                fsmString => fsmString.Value,
                EntityHostFsmData.Type.Strings,
                data.Strings
            );
            CondAddData(
                fsm.FsmVariables.Vector2Variables, 
                snapshot.Vector2s,
                fsmVec2 => fsmVec2.Value,
                EntityHostFsmData.Type.Vector2s,
                data.Vec2s
            );
            CondAddData(
                fsm.FsmVariables.Vector3Variables, 
                snapshot.Vector3s,
                fsmVec3 => fsmVec3.Value,
                EntityHostFsmData.Type.Vector3s,
                data.Vec3s
            );

            if (data.Types.Count > 0) {
                _netClient.UpdateManager.AddEntityHostFsmData(Id, fsmIndex, data);
            }
        }
    }

    /// <summary>
    /// Sends a hooked action the original entered, so the replicas can run it too.
    /// </summary>
    /// <param name="self">The FSM action instance that was entered.</param>
    private void PublishAction(FsmStateAction self) {
        if (!_hookedActions.TryGetValue(self, out var hookedEntityAction)) {
            return;
        }

        Logger.Debug(
            $"Entity ({Id}, {Type}) hooked action: {self.Fsm.Name}, {self.State.Name}, {self.GetType()} ({hookedEntityAction.FsmIndex}, {hookedEntityAction.StateIndex}, {hookedEntityAction.ActionIndex})");

        var networkData = new EntityNetworkData {
            Type = EntityComponentType.Fsm
        };

        if (_fsms.Host.Count > 1) {
            networkData.Packet.Write((byte)hookedEntityAction.FsmIndex);
        }

        networkData.Packet.Write((byte)hookedEntityAction.StateIndex);
        networkData.Packet.Write((byte)hookedEntityAction.ActionIndex);

        // Only if the GetNetworkDataFromAction method returns true do we add the entity data
        // for sending
        if (EntityFsmActions.GetNetworkDataFromAction(networkData, self)) {
            _netClient.UpdateManager.AddEntityData(Id, networkData);
        }
    }

    /// <summary>
    /// Sends an animation the original played, so the replicas play it too.
    /// </summary>
    private void PublishAnimation(tk2dSpriteAnimationClip clip) {
        if (!_animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
            Logger.Warn($"Entity '{Object.Client.name}' played unknown animation: {clip.name}");
            return;
        }

        Logger.Debug($"Entity '{Object.Host.name}' sends animation: {clip.name}, {animationId}, {clip.wrapMode}");
        _netClient.UpdateManager.UpdateEntityAnimation(
            Id,
            animationId,
            (byte)clip.wrapMode
        );
    }

    /// <summary>
    /// How often the scene host sends the whole presentation again even without a change, so a lost message is
    /// corrected by the next one.
    /// </summary>
    private const float BodyStateRefreshSeconds = 2f;

    /// <summary>The presentation this client last sent as scene host.</summary>
    private BodyState _publishedBodyState;

    private float _publishedBodyStateAt;

    /// <summary>The presentation the scene host last sent, or null before the first one arrives.</summary>
    private BodyState _bodyState;

    /// <summary>
    /// Sends how the original appears whenever that changes and again at intervals.
    /// </summary>
    private void PublishBodyState() {
        var state = BodyState.Of(Object.Host, IsCarried(Object.Host), _detached);
        var due = Time.unscaledTime - _publishedBodyStateAt > BodyStateRefreshSeconds;
        if (!due && state.SameAs(_publishedBodyState)) return;
        _publishedBodyState = state;
        _publishedBodyStateAt = Time.unscaledTime;
        var data = new EntityNetworkData { Type = EntityComponentType.Body };
        state.Write(data.Packet);
        _netClient.UpdateManager.AddEntityData(Id, data);
    }

    /// <summary>
    /// Whether another entity still holds the given body, such as a summon that has not released its enemy yet.
    /// An arena controller holds every enemy of its arena and does not count, and neither does the entity this one is
    /// declared a child of, such as Hornet for her needle: the needle is part of her, not held until released.
    /// </summary>
    private bool IsCarried(GameObject body) {
        var parent = body.transform.parent;
        if (_hasParent && parent != null) {
            parent = parent.parent;
        }

        for (; parent != null; parent = parent.parent) {
            if (!RegisteredEntities.IsEntity(parent.gameObject)) {
                continue;
            }

            if (EntityRegistry.TryGetEntry(parent.gameObject, out var entry) && entry.Type == EntityType.BattleScene) {
                continue;
            }

            return true;
        }

        return false;
    }
}
