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
/// The replica side of an entity: applies what the scene host sends.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Updates the position of the client entity.
    /// </summary>
    /// <param name="position">The new position.</param>
    public void UpdatePosition(Vector2 position) {
        if (Object.Client == null || Object.Host == null) {
            Logger.Warn($"Cannot update position for entity ({Id}, {Type}), client or host object is null");
            return;
        }
        
        var unityPos = new Vector3(
            position.X, 
            position.Y,
            _hasParent ? Object.Host.transform.localPosition.z : Object.Host.transform.position.z
        );

        _receivedPosition = unityPos;
        _hasReceivedPosition = true;

        var positionInterpolation = Object.Client.GetComponent<PositionInterpolation>();
        if (positionInterpolation == null) {
            return;
        }

        positionInterpolation.SetNewPosition(unityPos);
    }

    /// <summary>
    /// Updates the scale of the client entity.
    /// </summary>
    /// <param name="scale">The new scale data.</param>
    public void UpdateScale(EntityUpdate.ScaleData scale) {
        if (Object.Client == null) {
            Logger.Warn($"Cannot update scale for entity ({Id}, {Type}), client object is null");
            return;
        }
        
        var transform = Object.Client.transform;
        var localScale = transform.localScale;
        
        if (scale.x) {
            if (scale.xFlipped) {
                var currentScaleX = localScale.x;

                if (currentScaleX > 0 != scale.xPos) {
                    currentScaleX *= -1;

                    localScale.x = currentScaleX;
                }
            } else {
                localScale.x = scale.xScale;
            }
        }
        
        if (scale.y) {
            if (scale.yFlipped) {
                var currentScaleY = localScale.y;

                if (currentScaleY > 0 != scale.yPos) {
                    currentScaleY *= -1;

                    localScale.y = currentScaleY;
                }
            } else {
                localScale.y = scale.yScale;
            }
        }

        if (scale.z) {
            if (scale.zFlipped) {
                var currentScaleZ = localScale.z;

                if (currentScaleZ > 0 != scale.zPos) {
                    currentScaleZ *= -1;

                    localScale.z = currentScaleZ;
                }
            } else {
                localScale.z = scale.zScale;
            }
        }

        transform.localScale = localScale;
    }

    /// <summary>
    /// Updates the animation of the client entity.
    /// </summary>
    /// <param name="animationId">The ID of the animation.</param>
    /// <param name="wrapMode">The wrap mode of the animation clip.</param>
    /// <param name="alreadyInSceneUpdate">Whether this update is when entering a new scene.</param>
    public void UpdateAnimation(
        byte animationId, 
        tk2dSpriteAnimationClip.WrapMode wrapMode, 
        bool alreadyInSceneUpdate
    ) {
        if (_animator.Client == null) {
            Logger.Warn($"Entity '{Object.Client.name}' received animation while client animator does not exist");
            return;
        }

        if (!_animationClipNameIds.TryGetValue(animationId, out var clipName)) {
            Logger.Warn($"Entity '{Object.Client.name}' received unknown animation ID: {animationId}");
            return;
        }

        Logger.Debug($"Entity '{Object.Client.name}' received animation: {animationId}, {clipName}, {wrapMode}");

        // All paths lead to calling the Play method of the sprite animator that is hooked, so we allow the call
        // through the hook
        _allowClientAnimation = true;

        if (alreadyInSceneUpdate) {
            // Since this is an animation update from an entity that was already present in a scene,
            // we need to determine where to start playing this specific animation
            LateUpdateAnimation(_animator.Client, clipName, wrapMode);
        }

        // Otherwise, default to just playing the clip
        _animator.Client.Play(clipName);
    }

    /// <summary>
    /// Update the animation for the given animator with the given clip name and wrap mode. This assumes that we need
    /// to replicate animation behaviour for a late update.
    /// </summary>
    /// <param name="animator">The sprite animator to update.</param>
    /// <param name="clipName">The name of the animation clip.</param>
    /// <param name="wrapMode">The wrap mode for the animation.</param>
    private void LateUpdateAnimation(
        tk2dSpriteAnimator animator, 
        string clipName, 
        tk2dSpriteAnimationClip.WrapMode wrapMode
    ) {
        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.Loop) {
            animator.Play(clipName);
            return;
        }

        var clip = animator.GetClipByName(clipName);
        
        Logger.Debug($"Entity ({Id}, {Type}) LateUpdateAnimation: {clip.name}, {wrapMode}");

        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.LoopSection) {
            // The clip loops in a specific section in the frames, so we start playing
            // it from the start of that section
            animator.PlayFromFrame(clipName, clip.loopStart);
            return;
        }

        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.Once ||
            wrapMode == tk2dSpriteAnimationClip.WrapMode.Single) {
            // Since the clip was played once, it stops on the last frame,
            // so we emulate that by only "playing" the last frame of the clip
            var clipLength = clip.frames.Length;
            animator.PlayFromFrame(clipName, clipLength - 1);

            // Logger.Info(
            // $"  Played animation: {clipName}, {clipLength - 1} on {_animator.Client.name}, {_animator.Client.GetHashCode()}");
        }
    }

    /// <summary>
    /// Draws the replica the way the scene host shows the entity: visible only when the host shows it alive and
    /// released, with the same body and animation.
    /// </summary>
    private void ApplyBodyState(BodyState state) {
        _bodyState = state;
        if (IsHost || Object.Client == null) return;

        Object.Client.SetActive(state.Visible);
        var collider = Object.Client.GetComponent<Collider2D>();
        if (collider != null) collider.enabled = state.Collider;
        var renderer = Object.Client.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = state.Renderer;

        var animator = _animator.Client;
        if (state.Visible && animator != null && state.Clip.Length > 0
            && (animator.CurrentClip == null || animator.CurrentClip.name != state.Clip)) {
            var clip = animator.GetClipByName(state.Clip);
            if (clip != null) {
                _allowClientAnimation = true;
                animator.Play(clip);
                _allowClientAnimation = false;
            }
        }
    }

    /// <summary>
    /// Updates whether the game object for the client entity is active.
    /// </summary>
    /// <param name="active">The new value for active.</param>
    public void UpdateIsActive(bool active) {
        // The presentation is the whole truth; an activity report must not show what the host hides.
        if (active && _bodyState != null && !_bodyState.Visible) active = false;
        if (Object.Client != null) {
            Logger.Debug($"Entity '{Object.Client.name}' received active: {active}");
            Object.Client.SetActive(active);
        } else {
            Logger.Warn($"Entity ({Id}, {Type}) could not update active, because client object is null");
        }
    }

    /// <summary>
    /// Updates generic data for the client entity.
    /// </summary>
    /// <param name="entityNetworkData">A list of data to update the client entity with.</param>
    /// <param name="alreadyInSceneUpdate">Whether this data is from an already in scene update.</param>
    public void UpdateData(List<EntityNetworkData> entityNetworkData, bool alreadyInSceneUpdate) {
        foreach (var data in entityNetworkData) {
            if (data.Type == EntityComponentType.Body) {
                ApplyBodyState(BodyState.Read(data.Packet));
                continue;
            }

            if (data.Type == EntityComponentType.Fsm) {
                PlayMakerFSM fsm;
                byte stateIndex;
                byte actionIndex;

                // A controller kept without a replica, such as an arena, has no client machines to replay on.
                if (_fsms.Client.Count == 0) {
                    continue;
                }

                if (_fsms.Client.Count > 1) {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 3) {
                        continue;
                    }

                    var fsmIndex = data.Packet.ReadByte();
                    fsm = _fsms.Client[fsmIndex];

                    stateIndex = data.Packet.ReadByte();
                    actionIndex = data.Packet.ReadByte();
                } else {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 2) {
                        continue;
                    }

                    fsm = _fsms.Client[0];

                    stateIndex = data.Packet.ReadByte();
                    actionIndex = data.Packet.ReadByte();
                }

                var state = fsm.FsmStates[stateIndex];
                var action = state.Actions[actionIndex];
                
                Logger.Info($"Received entity network data for FSM: {fsm.Fsm.Name}, {state.Name}, {actionIndex} ({action.GetType()})");

                EntityFsmActions.ApplyNetworkDataFromAction(data, action);

                continue;
            }

            if (_components.TryGetValue(data.Type, out var component)) {
                component.Update(data, alreadyInSceneUpdate);
            }
        }
    }

    /// <summary>
    /// Update the FSMs of the host entity to prepare for host transfer or disconnects.
    /// </summary>
    /// <param name="hostFsmData">Dictionary mapping FSM index to data.</param>
    public void UpdateHostFsmData(Dictionary<byte, EntityHostFsmData> hostFsmData) {
        foreach (var fsmPair in hostFsmData) {
            var fsmIndex = fsmPair.Key;
            var data = fsmPair.Value;

            if (_fsms.Host.Count <= fsmIndex) {
                Logger.Warn($"Tried to update host FSM data for unknown FSM index: {fsmIndex}");
                continue;
            }

            var hostFsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

            if (data.Types.Contains(EntityHostFsmData.Type.State)) {
                var states = hostFsm.FsmStates;
                if (states.Length <= data.CurrentState) {
                    Logger.Warn($"Tried to update host FSM state for unknown state index: {data.CurrentState}");
                } else {
                    var stateName = states[data.CurrentState].Name;
                    
                    snapshot.CurrentState = stateName;
                    
                    // Also propagate this state change to the EntityFsmActions class with the client FSM for the
                    // same index
                    if (_fsms.Client.Count > fsmIndex) {
                        EntityFsmActions.RegisterStateChange(_fsms.Client[fsmIndex].Fsm, stateName);
                    }
                }
            }

            var fsms = _fsms.Client.Count > fsmIndex
                ? new[] { hostFsm, _fsms.Client[fsmIndex] }
                : new[] { hostFsm };

            void CondUpdateVars<TFsm, TBase>(
                EntityHostFsmData.Type type,
                Dictionary<byte, TBase> dataDict,
                TFsm[] fsmVarArray,
                Action<byte, TFsm, TBase> setValueAction
            ) {
                if (data.Types.Contains(type)) {
                    foreach (var pair in dataDict) {
                        if (fsmVarArray.Length <= pair.Key) {
                            Logger.Warn($"Tried to update host FSM var ({typeof(TBase)}) for unknown index: {pair.Key}");
                        } else {
                            setValueAction.Invoke(pair.Key, fsmVarArray[pair.Key], pair.Value);
                        }
                    }
                }
            }

            // A controller that keeps running on every player must not have its own values overwritten; the host's
            // values are only kept in the snapshot for a handover, through a scratch copy of its variables.
            var variableSets = fsms.Select(machine => KeepsOriginalRunning && machine == hostFsm
                ? new FsmVariables(machine.FsmVariables)
                : machine.FsmVariables);
            foreach (var variables in variableSets) {
                CondUpdateVars(
                    EntityHostFsmData.Type.Floats,
                    data.Floats,
                    variables.FloatVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Floats[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Ints,
                    data.Ints,
                    variables.IntVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Ints[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Bools,
                    data.Bools,
                    variables.BoolVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Bools[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Strings,
                    data.Strings,
                    variables.StringVariables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = value;
                        snapshot.Strings[index] = value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Vector2s,
                    data.Vec2s,
                    variables.Vector2Variables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = (UnityEngine.Vector2) value;
                        snapshot.Vector2s[index] = (UnityEngine.Vector2) value;
                    }
                );
                CondUpdateVars(
                    EntityHostFsmData.Type.Vector3s,
                    data.Vec3s,
                    variables.Vector3Variables,
                    (index, fsmVar, value) => {
                        fsmVar.Value = (Vector3) value;
                        snapshot.Vector3s[index] = (Vector3) value;
                    }
                );
            }
        }
    }

}
