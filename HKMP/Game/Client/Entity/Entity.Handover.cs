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
/// Moves an entity between the roles when the scene host changes.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Makes the entity a client entity again if the server moved the scene host role away from this player while
    /// it was stalled. The client copy takes over where the host object was and follows the new host from then on.
    /// </summary>
    public void MakeClient() {
        Logger.Info($"Making entity ({Id}, {Type}) a client entity");

        // A later handover must use what the next host shows, not what this client showed.
        _bodyState = null;

        ActivateGameObject.DoActivateGameObject -= OnDoActivateGameObject;

        HandBodyToReplica();
        AdoptReplicaRole();
    }

    /// <summary>
    /// Puts the replica where the original is, as it is shown, and switches the original off.
    /// </summary>
    private void HandBodyToReplica() {
        if (Object.Host != null && Object.Client != null) {
            var hostActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
            // If the role returns before the new host moves this entity, the handover must not use an older position
            _detached = OriginalIsDetached();
            _receivedPosition = UsesParentSpace ? Object.Host.transform.localPosition : Object.Host.transform.position;
            _hasReceivedPosition = true;
            MatchHierarchy(Object.Client, _declaredReplicaParent, _detached);
            Object.Client.transform.position = Object.Host.transform.position;
            Object.Client.transform.localScale = Object.Host.transform.lossyScale;
            if (!KeepsOriginalRunning) Object.Host.SetActive(false);
            Object.Client.SetActive(hostActive);
        } else if (Object.Host != null && !KeepsOriginalRunning) {
            Object.Host.SetActive(false);
        }
    }

    /// <summary>
    /// From here on the replica follows the new scene host.
    /// </summary>
    private void AdoptReplicaRole() {
        SwitchRole(host: false);

        foreach (var component in _components.Values) {
            component.IsControlled = true;
        }
    }

    /// <summary>
    /// Makes the entity a host entity if the client user became the scene host.
    /// </summary>
    /// <remarks>
    /// This is the first of two phases. Every entity of the room is prepared before any of them enters its states
    /// again in <see cref="ReenterAfterHandover"/>, so no machine runs while another one, such as the boss a
    /// projectile reports to, is not ready yet.
    /// </remarks>
    /// <param name="deaths">The entities of the room known to be dead, from the server's authority over it.</param>
    public void MakeHost(KnownDeaths deaths) {
        Logger.Info($"Making entity ({Id}, {Type}) a host entity");

        _awaitsReentry = false;
        // A dead entity comes first: its replica can already be gone, and the paths below for a missing object only
        // switch the original off, which left it alive for the game.
        if (TakesOverDead(deaths) || EndsWithoutTakingOver()) {
            return;
        }

        PlaceBody();
        RestoreActivity();
        RestorePhysicsBody();
        AdoptHostRole();
        ResumeAnimation();
        PrepareMachines();
        _awaitsReentry = true;
    }

    /// <summary>
    /// The second phase of taking an entity over: enters each machine in its snapshot state again and settles the
    /// body. Called once every entity of the room was prepared by <see cref="MakeHost"/>.
    /// </summary>
    public void ReenterAfterHandover() {
        if (!_awaitsReentry) {
            return;
        }

        _awaitsReentry = false;
        ReenterMachines();
        SettleBody();
    }

    /// <summary>Whether <see cref="MakeHost"/> prepared this entity and its machines still have to enter again.</summary>
    private bool _awaitsReentry;

    /// <summary>
    /// The body state a handover in progress applies: the previous host's, or the replica's when none arrived.
    /// </summary>
    private BodyState _handoverBodyState;

    /// <summary>
    /// Handles the entities there is nothing to take over for, and says whether one of them was this one.
    /// </summary>
    private bool EndsWithoutTakingOver() {
        // A projectile or other body spawned during play cannot be resumed: what drives it, such as its flight
        // and lifetime, ran on the previous host only, so it would hang in place and keep hurting players. It is
        // ended instead, and reported inactive so nobody entering the room later receives it again. Spawned
        // enemies, which have health, are taken over like any other.
        if (SpawnedDuringPlay && Object.Host != null && Object.Host.GetComponent<HealthManager>() == null) {
            Logger.Info($"  Spawned entity ({Id}, {Type}) ended at the handover");
            Object.Host.SetActive(false);
            Object.Client?.SetActive(false);
            SwitchRole(host: true);
            foreach (var component in _components.Values) {
                component.IsControlled = false;
            }

            _lastIsActive = false;
            _netClient.UpdateManager.UpdateEntityIsActive(Id, false);
            return true;
        }

        // If the client object is null, we don't have to care about doing anything for the host object anymore
        if (Object.Client == null) {
            if (Object.Host != null) {
                Object.Host.SetActive(false);
            }

            SwitchRole(host: true);

            foreach (var component in _components.Values) {
                component.IsControlled = false;
            }

            Logger.Debug("  Client object null, enabling host object and returning");
            return true;
        }

        // The host object can be destroyed while the client object still exists, for example when a battle
        // scene destroys its enemies on the previous scene host. There is nothing left to hand over then.
        if (Object.Host == null) {
            Object.Client.SetActive(false);

            SwitchRole(host: true);

            foreach (var component in _components.Values) {
                component.IsControlled = false;
            }

            Logger.Debug("  Host object null, disabling client object and returning");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Takes over a dead entity dead: the original stays switched off and dead, and its machines are not resumed.
    /// </summary>
    /// <remarks>
    /// The server's list decides, and so does the original's own death on this client, since a player that killed
    /// the entity while hosting has it dead before the server's list reaches it.
    /// </remarks>
    /// <param name="deaths">The entities of the room known to be dead.</param>
    /// <returns>Whether the entity was dead.</returns>
    private bool TakesOverDead(KnownDeaths deaths) {
        if (Object.Host == null || !_components.TryGetValue(EntityComponentType.Health, out var component)
            || !(component is HealthManagerComponent health)) {
            return false;
        }

        // Logged for every entity taken over, so a trace shows whether the original and the server counted it dead.
        Logger.Info($"  Entity ({Id}, {Type}) at the handover: {health.OriginalState}, known dead: {deaths.Contains(Id)}");
        if (!deaths.Contains(Id) && !health.OriginalDied) {
            return false;
        }

        if (Object.Client != null) {
            Object.Client.SetActive(false);
        }

        Object.Host.SetActive(false);
        AdoptHostRole();
        health.AdoptDeath();
        Logger.Info($"  Entity ({Id}, {Type}) was taken over dead");
        return true;
    }

    /// <summary>
    /// Places the original where the previous scene host last showed the entity, at the same scale.
    /// </summary>
    private void PlaceBody() {
        // A child the game took out of its parent on the previous host is taken out here too, before it is placed,
        // or it would be placed relative to the parent and follow it.
        _detached = _bodyState != null && _bodyState.Detached;
        MatchHierarchy(Object.Host, _declaredParent, _detached);

        if (UsesParentSpace) {
            Logger.Debug("  Entity has parent, only setting local transform");

            Object.Host.transform.localPosition = _lastPosition =
                HandoverPosition.Choose(_hasReceivedPosition, _receivedPosition, _initialPosition);
            Object.Host.transform.localScale = _lastScale = Object.Client.transform.localScale;
        } else {
            Logger.Debug("  Entity has no parent, calculating transform");

            var clientPos = HandoverPosition.Choose(_hasReceivedPosition, _receivedPosition, _initialPosition);

            // Subtracting the parent's position ignores a scaled or rotated parent and moved such enemies far from
            // where they were, sometimes under the floor; the world position is placed directly instead.
            Object.Host.transform.position = clientPos;
            _lastPosition = Object.Host.transform.position;

            // Since the scale of the client object is the entire scale we have and the host object scale can be in a
            // hierarchy, we need to calculate what the new local scale of the host will be to match the client scale
            var clientScale = Object.Client.transform.localScale;
            var hostLocalScale = Object.Host.transform.localScale;
            var hostLossyScale = Object.Host.transform.lossyScale;

            var newScaleX = hostLocalScale.x == 0 || hostLossyScale.x == 0
                ? 0f
                : clientScale.x / (hostLossyScale.x / hostLocalScale.x);
            var newScaleY = hostLocalScale.y == 0 || hostLossyScale.y == 0
                ? 0f
                : clientScale.y / (hostLossyScale.y / hostLocalScale.y);
            var newScaleZ = hostLocalScale.z == 0 || hostLossyScale.z == 0
                ? 0f
                : clientScale.z / (hostLossyScale.z / hostLocalScale.z);

            Object.Host.transform.localScale = _lastScale = new Vector3(newScaleX, newScaleY, newScaleZ);
        }
    }

    /// <summary>
    /// Switches the original on or off as the previous scene host showed it, and the replica off.
    /// </summary>
    private void RestoreActivity() {
        _handoverBodyState = _bodyState ?? BodyState.Of(Object.Client, carried: false, detached: _detached);
        if (_bodyState == null) {
            Logger.Info($"  Entity ({Id}, {Type}) has no body state from the previous host; the replica's body stands in");
        }

        // Make sure that the sprite animator doesn't play the default clip after enabling the object
        if (_animator.Host != null) {
            _animator.Host.playAutomatically = false;
        }

        var clientHealth = Object.Client.GetComponent<HealthManager>();
        var clientActive = HandoverActivity.OriginalActive(KeepsOriginalRunning, Object.Host.activeSelf,
            _bodyState?.Shown, _bodyState?.Alive, Object.Client.activeSelf,
            clientHealth != null && clientHealth.GetIsDead());
        Object.Client.SetActive(false);
        Object.Host.SetActive(clientActive);

        Logger.Debug($"  Set Active of host object to: {clientActive}, disabling client object");
    }

    /// <summary>
    /// Sets the body as the previous scene host last had it: collider, renderer, physics, invincibility and contact
    /// damage. Without a state from the previous host, the replica's own body stands in, since it follows that host.
    /// </summary>
    private void RestorePhysicsBody() {
        _handoverBodyState.ApplyTo(Object.Host, $"({Id}, {Type})");

        // The kinematic component's own report is now part of the body state.
        if (_components.TryGetValue(EntityComponentType.Kinematic, out var component)) {
            (component as KinematicComponent)?.TakenOver();
        }
    }

    /// <summary>
    /// From here on this client runs the original and sends what it does.
    /// </summary>
    private void AdoptHostRole() {
        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        // From now on this client sends positions; a later handover must wait for the next host's own.
        _hasReceivedPosition = false;

        SwitchRole(host: true);

        foreach (var component in _components.Values) {
            component.IsControlled = false;
        }
    }

    /// <summary>
    /// Shows on the original the clip the replica was showing.
    /// </summary>
    private void ResumeAnimation() {
        if (_animator.Client != null) {
            var currentClip = _animator.Client.CurrentClip;
            if (currentClip != null) {
                var clientAnimation = currentClip.name;
                var wrapMode = currentClip.wrapMode;

                Logger.Debug($"  Animator and current clip present, updating animation: {clientAnimation}, {wrapMode}");

                LateUpdateAnimation(_animator.Host, clientAnimation, wrapMode);   
            }
        }
    }

    /// <summary>
    /// Initializes each machine when needed and restores its snapshot, without entering any state yet.
    /// </summary>
    private void PrepareMachines() {
        Logger.Debug("  Restoring FSMs from snapshots");

        var initialize = HandoverInitialization.ShouldInitialize(_originalInitialized);
        Logger.Info($"  Entity ({Id}, {Type}) {(initialize ? "initializes its machines for the first time" : "keeps the setup of its machines")}");

        // The game initializes a machine where the room placed the body, and some initializations fix things in
        // the world from there, such as the range a burrowed boss waits on before rising. The first initialization
        // runs at that place too, and the body goes back to where the handover put it afterwards.
        // A body taken out of its parent has no room place in its current space, so it stays where it is.
        var placed = initialize && !(_hasParent && _detached) ? MoveToInitialPlace() : (Vector3?) null;
        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            InitializeMachine(_fsms.Host[fsmIndex], initialize);
        }
        if (placed.HasValue) {
            SetPlace(placed.Value);
        }

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            Logger.Debug($"    Restoring FSM: {_fsms.Host[fsmIndex].Fsm.Name}");
            RestoreSnapshot(_fsms.Host[fsmIndex], fsmIndex);
        }

        _originalInitialized = true;
    }

    /// <summary>Moves the original to where the room placed it, and returns where it was.</summary>
    private Vector3 MoveToInitialPlace() {
        var placed = UsesParentSpace ? Object.Host.transform.localPosition : Object.Host.transform.position;
        SetPlace(_initialPosition);
        return placed;
    }

    private void SetPlace(Vector3 place) {
        if (UsesParentSpace) {
            Object.Host.transform.localPosition = place;
        } else {
            Object.Host.transform.position = place;
        }
    }

    /// <summary>
    /// Enters every machine of the entity in its snapshot state again, once all of them hold their snapshot.
    /// </summary>
    private void ReenterMachines() {
        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            Reenter(_fsms.Host[fsmIndex], fsmIndex);
        }
    }

    /// <summary>
    /// Initializes the machine the first time, or only prepares it when it already ran on this client.
    /// </summary>
    private static void InitializeMachine(PlayMakerFSM fsm, bool initialize) {
        // An original that never ran is initialized here once; repeating it would undo one-time setup, such as a
        // child the first initialization detached from the body and kept a reference to.
        if (initialize) {
            EntityInitializer.InitializeFsm(fsm);
        } else {
            EntityInitializer.CheckPreProcessFsm(fsm);
        }
    }

    /// <summary>
    /// Copies the previous scene host's variables into the machine and resolves its object references.
    /// </summary>
    private void RestoreSnapshot(PlayMakerFSM fsm, int fsmIndex) {
        var snapshot = _fsmSnapshots[fsmIndex];

        for (var i = 0; i < snapshot.Floats.Length; i++) {
            fsm.FsmVariables.FloatVariables[i].Value = snapshot.Floats[i];
        }
        for (var i = 0; i < snapshot.Ints.Length; i++) {
            fsm.FsmVariables.IntVariables[i].Value = snapshot.Ints[i];
        }
        for (var i = 0; i < snapshot.Bools.Length; i++) {
            fsm.FsmVariables.BoolVariables[i].Value = snapshot.Bools[i];
        }
        for (var i = 0; i < snapshot.Strings.Length; i++) {
            fsm.FsmVariables.StringVariables[i].Value = snapshot.Strings[i];
        }
        for (var i = 0; i < snapshot.Vector2s.Length; i++) {
            fsm.FsmVariables.Vector2Variables[i].Value = snapshot.Vector2s[i];
        }
        for (var i = 0; i < snapshot.Vector3s.Length; i++) {
            fsm.FsmVariables.Vector3Variables[i].Value = snapshot.Vector3s[i];
        }

        HandoverFsmReferences.Fill(fsm, $"({Id}, {Type})");
    }

    /// <summary>
    /// Enters the snapshot state again, or its anchor, running the actions the re-entry table allows.
    /// </summary>
    private void Reenter(PlayMakerFSM fsm, int fsmIndex) {
        var snapshot = _fsmSnapshots[fsmIndex];

        // The machine enters its state again from the start, running every action except the ones that must not
        // happen twice, such as creating a projectile the previous host already created. A state that chooses its
        // way out chooses again, and the state's own effects, such as its animation, are made again.
        var stateName = ReentryRegistry.StateToEnter(Type, fsm.Fsm.Name, snapshot.CurrentState);
        var state = fsm.GetState(stateName);
        if (state == null) {
            Logger.Debug("  Not setting FSM state, because current state is empty");
            return;
        }

        var allActions = state.Actions;
        var runActions = ReentryRegistry.ActionsToRun(allActions);
        Logger.Info($"  Entity ({Id}, {Type}) enters '{fsm.Fsm.Name}' again in '{stateName}'" +
                    (stateName != snapshot.CurrentState ? $" (anchor of '{snapshot.CurrentState}')" : "") +
                    $", running {runActions.Length} of {allActions.Length} actions; left out: " +
                    string.Join(", ", allActions.Except(runActions).Select(a => a.GetType().Name)));

        state.Actions = runActions;
        fsm.SetState(stateName);
        state.Actions = allActions;

        // The clip the previous host last showed outranks the resumed state's own.
        if (fsmIndex == 0 && _bodyState != null && _bodyState.Clip.Length > 0 && _animator.Host != null) {
            var clip = _animator.Host.GetClipByName(_bodyState.Clip);
            if (clip != null) _animator.Host.Play(clip);
        }
    }

    /// <summary>
    /// Settles what resuming the machines can disturb: child hitboxes, the position and the health.
    /// </summary>
    private void SettleBody() {
        // Resuming a machine does not replay what earlier states switched on, such as Gruz Mother's Hero Damager,
        // so the children are set as the previous host had them before the new host simulates a frame.
        _handoverBodyState.ApplyChildrenTo(Object.Host, $"({Id}, {Type})");

        // Initialising and resuming the machines can run actions that place the body, such as a spawn that moves
        // the enemy to where it hides before entering, so the position chosen above is placed once more.
        if (UsesParentSpace) {
            Object.Host.transform.localPosition = _lastPosition;
        } else {
            Object.Host.transform.position = _lastPosition;
        }

        if (_components.TryGetValue(EntityComponentType.Health, out var health)) {
            ((HealthManagerComponent)health).RestoreHealth();
        }
    }


    /// <summary>
    /// Switches on the parents that keep this entity's original hidden after it was taken over.
    /// </summary>
    public void ShowParents() {
        // An enemy the previous host still had inside its carrier stays there, to be released as in normal play.
        if (_bodyState != null && _bodyState.Carried) {
            _shownAtHandover = false;
            return;
        }
        if (Object.Host != null && IsHost) HandoverParents.Show(Object.Host, $"({Id}, {Type})");
        _shownAtHandover = Object.Host != null && IsHost && Object.Host.activeInHierarchy;
    }

    /// <summary>
    /// Shows this entity again if its room hid it after the handover while it is still alive.
    /// </summary>
    /// <remarks>
    /// An arena resumed mid-fight can replay a state that switches its wave groups off, hiding enemies that were
    /// fighting. Only an enemy shown at the handover and not dead is brought back.
    /// </remarks>
    public void KeepShown() {
        if (!_shownAtHandover || !IsHost || Object.Host == null) return;
        var health = Object.Host.GetComponent<HealthManager>();
        if (health != null && health.GetIsDead()) {
            _shownAtHandover = false;
            return;
        }

        HandoverParents.Show(Object.Host, $"({Id}, {Type})");
    }

    /// <summary>
    /// Stops keeping this entity shown, once the room has settled after a handover.
    /// </summary>
    public void SettleAfterHandover() => _shownAtHandover = false;

}
