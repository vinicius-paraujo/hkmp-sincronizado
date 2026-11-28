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
/// A networked entity that is either sending behaviour updates to the server or is entirely controlled by
/// updates from the server.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Whether the entity has a parent entity.
    /// </summary>
    private readonly bool _hasParent;

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    public ushort Id { get; }

    /// <summary>
    /// The type of the entity.
    /// </summary>
    public EntityType Type { get; }

    /// <summary>
    /// Host-client pair for the game objects.
    /// </summary>
    public HostClientPair<GameObject> Object { get; }

    /// <summary>
    /// Host-client pair for the sprite animators.
    /// </summary>
    private readonly HostClientPair<tk2dSpriteAnimator> _animator;

    /// <summary>
    /// Bi-directional lookup for animation clip names to IDs.
    /// </summary>
    private readonly BiLookup<string, byte> _animationClipNameIds;

    /// <summary>
    /// Host-client pair for the lists of FSMs on the entity.
    /// </summary>
    private readonly HostClientPair<List<PlayMakerFSM>> _fsms;

    /// <summary>
    /// Dictionary mapping data types to entity components.
    /// </summary>
    private readonly Dictionary<EntityComponentType, EntityComponent> _components;

    /// <summary>
    /// Dictionary mapping FSM actions to their entity action data instances.
    /// </summary>
    private readonly Dictionary<FsmStateAction, HookedEntityAction> _hookedActions;
    /// <summary>
    /// Set of FSM action types that have been hooked to prevent duplicate hooks.
    /// </summary>
    private readonly HashSet<Type> _hookedTypes;

    /// <summary>
    /// Whether the unity game object for the host entity was originally active.
    /// </summary>
    private bool _originalIsActive;

    /// <summary>
    /// What this client does with the entity now: waits for the scene host to be known, runs the original as
    /// scene host, or follows the scene host with the replica.
    /// </summary>
    private Role _role;

    private readonly UndeterminedRole _undetermined;
    private readonly HostRole _host;
    private readonly ReplicaRole _replica;

    /// <summary>Whether this client runs the original as scene host.</summary>
    private bool IsHost => _role == _host;

    /// <summary>
    /// Whether the scene host is determined, or alternatively whether the entity has been determined to be a host or client entity.
    /// </summary>
    private bool _isSceneHostDetermined;

    /// <summary>
    /// The last position of the entity.
    /// </summary>
    private Vector3 _lastPosition;

    /// <summary>
    /// The last position received from the previous scene host, in the space the client object uses.
    /// </summary>
    private Vector3 _receivedPosition;

    /// <summary>
    /// Whether a position was received since this client last stopped being the scene host.
    /// </summary>
    private bool _hasReceivedPosition;

    /// <summary>
    /// Where the room placed the original object when the entity was created.
    /// </summary>
    private readonly Vector3 _initialPosition;
    /// <summary>
    /// The last scale of the entity.
    /// </summary>
    private Vector3 _lastScale;
    /// <summary>
    /// Whether the game object for the entity was last active.
    /// </summary>
    private bool _lastIsActive;


    /// <summary>
    /// The scene the original object belongs to, to tell a destroyed enemy from a room being unloaded.
    /// </summary>
    private UnityEngine.SceneManagement.Scene _hostScene;

    /// <summary>
    /// Whether this entity was shown when this client took it over, and is kept shown for a while after.
    /// </summary>
    private bool _shownAtHandover;

    /// <summary>
    /// Whether this entity was registered after its room loaded, because something spawned it during play.
    /// </summary>
    public bool SpawnedDuringPlay { get; set; }

    /// <summary>
    /// Whether the original keeps running on players that are not the scene host. An arena controller counts
    /// each player's own arrival there, so switching it off would keep a fight from ever starting; it is only kept
    /// in step through the snapshot, which a handover restores.
    /// </summary>
    private bool KeepsOriginalRunning => Type == EntityType.BattleScene;

    /// <summary>
    /// Whether the original object's machines have been initialized, by the game on the first scene host or by an
    /// earlier handover on this client. Initializing again would repeat one-time setup in the middle of a fight.
    /// </summary>
    private bool _originalInitialized;

    /// <summary>
    /// Whether to allow the client entity to animate itself.
    /// </summary>
    private bool _allowClientAnimation;

    /// <summary>
    /// List of snapshots for each FSM of a host entity that contain latest values for state and FSM variables.
    /// Used to check whether state/variables change and to update the server accordingly.
    /// </summary>
    private readonly List<FsmSnapshot> _fsmSnapshots;

    public Entity(
        NetClient netClient,
        ushort id,
        EntityType type,
        GameObject hostObject,
        GameObject clientObject = null,
        params EntityComponentType[] types
    ) {
        _netClient = netClient;
        Id = id;

        Type = type;

        _undetermined = new UndeterminedRole(this);
        _host = new HostRole(this);
        _replica = new ReplicaRole(this);
        SwitchRole(host: false);

        if (clientObject == null) {
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = UnityEngine.Object.Instantiate(
                    hostObject,
                    hostObject.transform.position,
                    hostObject.transform.rotation
                )
            };

            DestroyManagedChildren(Object.Client);

            _hasParent = false;
        } else {
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = clientObject
            };

            _hasParent = true;
        }

        Object.Client.transform.localScale = _lastScale = _hasParent 
            ? Object.Host.transform.localScale 
            : Object.Host.transform.lossyScale;

        RememberDeclaredParents();

        RegisteredEntities.Add(Object.Host);
        RegisteredEntities.Add(Object.Client);

        _hostScene = Object.Host.scene;

        // The room's own placement of the original object, before any physics can move it on this client.
        _initialPosition = _hasParent ? Object.Host.transform.localPosition : Object.Host.transform.position;

        // Store whether the host object was active and set it not active until we know if we are scene host
        _originalIsActive = Object.Host.activeSelf;

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        Logger.Debug(
            $"Entity '{Object.Host.name}' was original active: {_originalIsActive}, last active: {_lastIsActive}");

        // Add a position interpolation component to the enemy so we can smooth out position updates
        Object.Client.AddComponent<PositionInterpolation>();

        // Register an update event to send position updates and check for certain value changes
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;

        _animator = new HostClientPair<tk2dSpriteAnimator> {
            Host = Object.Host.GetComponent<tk2dSpriteAnimator>(),
            Client = Object.Client.GetComponent<tk2dSpriteAnimator>()
        };
        if (_animator.Host != null) {
            _animationClipNameIds = new BiLookup<string, byte>();

            var index = 0;
            foreach (var animationClip in _animator.Host.Library.clips) {
                if (_animationClipNameIds.ContainsFirst(animationClip.name)) {
                    continue;
                }

                _animationClipNameIds.Add(animationClip.name, (byte)index++);

                if (index > byte.MaxValue) {
                    Logger.Error($"Too many animation clips to fit in a byte for entity: {Object.Client.name}");
                    break;
                }
            }

            On.tk2dSpriteAnimator.Play_tk2dSpriteAnimationClip_float_float += OnAnimationPlayed;
        }
        
        // Always disallow the client object from being recycled, because it will simply be destroyed
        On.ObjectPool.Recycle_GameObject += ObjectPoolOnRecycleGameObject;
        
        // Register a hook for the ActivateGameObject action to update the active state of the host game object
        // before scene host is determined
        ActivateGameObject.DoActivateGameObject += OnDoActivateGameObject;

        _fsms = new HostClientPair<List<PlayMakerFSM>> {
            Host = Object.Host.GetComponents<PlayMakerFSM>().ToList(),
            Client = Object.Client.GetComponents<PlayMakerFSM>().ToList()
        };

        _hookedActions = new Dictionary<FsmStateAction, HookedEntityAction>();
        _hookedTypes = new HashSet<Type>();
        _fsmSnapshots = new List<FsmSnapshot>();
        foreach (var fsm in _fsms.Host) {
            ProcessHostFsm(fsm);
        }

        // Remove all components that (re-)activate FSMs
        foreach (var fsmActivator in Object.Client.GetComponents<FSMActivator>()) {
            fsmActivator.StopAllCoroutines();
            UnityEngine.Object.Destroy(fsmActivator);
        }

        foreach (var fsm in _fsms.Client) {
            ProcessClientFsm(fsm);
        }

        _components = new Dictionary<EntityComponentType, EntityComponent>();
        HandleComponents(types);
        
        EntityDeathEffects.RemoveFromReplica(Type, Object.Client, $"({Id}, {Type})");

        if (!KeepsOriginalRunning) Object.Host.SetActive(false);
        Object.Client.SetActive(false);
        
        GodhomeBossDeath.Register(Object.Host, Object.Client, $"({Id}, {Type})");
        
        // // Debug code that logs each action's OnEnter method call
        // foreach (var fsm in _fsms.Host) {
        //     foreach (var state in fsm.FsmStates) {
        //         foreach (var action in state.Actions) {
        //             FsmActionHooks.RegisterFsmStateActionType(action.GetType(), stateAction => {
        //                 if (stateAction != action) {
        //                     return;
        //                 }
        //                 
        //                 Logger.Debug($"Entity ({Id}, {Type}) has host FSM enter action: {state.Name}, {action.GetType()}, {state.Actions.ToList().IndexOf(action)}");
        //             });
        //         }
        //     }
        // }
    }

    /// <summary>
    /// Destroy the children of the given game object that are registered entities in the system themselves.
    /// Recursively go through the non-registered children as well.
    /// </summary>
    /// <param name="root">The root game object to start searching for children.</param>
    private void DestroyManagedChildren(GameObject root) {
        foreach (var child in root.GetChildren()) {
            if (EntityRegistry.TryGetEntry(child, out var entry)) {
                Logger.Debug($"Found managed child: {child.name}, {entry.Type}, destroying it");
                UnityEngine.Object.Destroy(child);
            } else {
                DestroyManagedChildren(child);
            }
        }
    }

    /// <summary>
    /// Processes the given FSM for the host entity by hooking supported FSM actions.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessHostFsm(PlayMakerFSM fsm) {
        Logger.Debug($"Processing host FSM: {fsm.Fsm.Name}");

        EntityInitializer.CheckPreProcessFsm(fsm);
        
        for (var i = 0; i < fsm.FsmStates.Length; i++) {
            var state = fsm.FsmStates[i];
            var stateName = state.Name;

            for (var j = 0; j < state.Actions.Length; j++) {
                var action = state.Actions[j];
                if (!action.Enabled) {
                    continue;
                }

                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (action.Fsm == null) {
                    Logger.Error($"FSM in action for state ({i}, {state.Name}), action ({j}, {action.GetType()}) is null");
                    continue;
                }

                _hookedActions[action] = new HookedEntityAction {
                    Action = action,
                    FsmIndex = _fsms.Host.IndexOf(fsm),
                    StateIndex = i,
                    ActionIndex = j
                };
                Logger.Debug(
                    $"Created hooked action: {action.GetType()}, {_fsms.Host.IndexOf(fsm)}, {stateName}, {j}");

                if (_hookedTypes.Add(action.GetType())) {
                    FsmActionHooks.RegisterFsmStateActionType(action.GetType(), OnActionEntered);
                }
            }
        }

        var snapshot = new FsmSnapshot {
            CurrentState = fsm.ActiveStateName
        };

        snapshot.Floats = fsm.FsmVariables.FloatVariables.Select(f => f.Value).ToArray();
        snapshot.Ints = fsm.FsmVariables.IntVariables.Select(i => i.Value).ToArray();
        snapshot.Bools = fsm.FsmVariables.BoolVariables.Select(b => b.Value).ToArray();
        snapshot.Strings = fsm.FsmVariables.StringVariables.Select(s => s.Value).ToArray();
        snapshot.Vector2s = fsm.FsmVariables.Vector2Variables.Select(v => v.Value).ToArray();
        snapshot.Vector3s = fsm.FsmVariables.Vector3Variables.Select(v => v.Value).ToArray();

        _fsmSnapshots.Add(snapshot);
    }

    /// <summary>
    /// Processes the given FSM for the client entity by disabling it.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessClientFsm(PlayMakerFSM fsm) {
        Logger.Debug($"Processing client FSM: {fsm.Fsm.Name}");
        EntityInitializer.InitializeFsm(fsm);
        fsm.enabled = false;
    }

    /// <summary>
    /// Check the host and client objects for components that are supported for networking.
    /// </summary>
    private void HandleComponents(EntityComponentType[] types) {
        var addedComponentsString = $"Adding components to entity ({Object.Host.name}, {Id}):";
        
        var hostHealthManager = Object.Host.GetComponent<HealthManager>();
        var clientHealthManager = Object.Client.GetComponent<HealthManager>();
        if (hostHealthManager != null && clientHealthManager != null) {
            var healthManager = new HostClientPair<HealthManager> {
                Host = hostHealthManager,
                Client = clientHealthManager
            };

            var hmComponent = new HealthManagerComponent(
                _netClient,
                Id,
                Type,
                Object,
                healthManager
            );
            _components[EntityComponentType.Death] = hmComponent;
            _components[EntityComponentType.Invincibility] = hmComponent;
            _components[EntityComponentType.Health] = hmComponent;

            // Check if the object from the health manager is in any of the colosseum trial scenes and remove the
            // geo drops from them if so
            var goScene = hostHealthManager.gameObject.scene.name;
            if (goScene is "Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold") {
                clientHealthManager.SetGeoSmall(0);
                clientHealthManager.SetGeoMedium(0);
                clientHealthManager.SetGeoLarge(0);
            }

            addedComponentsString += " Death Invincibility";
        }

        var climber = Object.Client.GetComponent<Climber>();
        if (climber != null) {
            _components[EntityComponentType.Climber] = new ClimberComponent(
                _netClient,
                Id,
                Object,
                climber
            );
            _components[EntityComponentType.Rotation] = new RotationComponent(
                _netClient,
                Id,
                Object
            );
            
            addedComponentsString += " Climber Rotation";
        }

        var hostCollider = Object.Host.GetComponent<Collider2D>();
        var clientCollider = Object.Client.GetComponent<Collider2D>();
        if (hostCollider != null && clientCollider != null) {
            Logger.Debug($"Adding collider component to entity: {Object.Host.name}");

            var collider = new HostClientPair<Collider2D> {
                Host = hostCollider,
                Client = clientCollider
            };

            _components[EntityComponentType.Collider] = new ColliderComponent(
                _netClient,
                Id,
                Object,
                collider
            );
            
            addedComponentsString += " Collider";
        }

        var hostRigidbody = Object.Host.GetComponent<Rigidbody2D>();
        if (hostRigidbody != null) {
            _components[EntityComponentType.Kinematic] = new KinematicComponent(
                _netClient,
                Id,
                Object,
                hostRigidbody
            );

            addedComponentsString += " Kinematic";
        }

        var hostDamageHero = Object.Host.GetComponent<DamageHero>();
        var clientDamageHero = Object.Client.GetComponent<DamageHero>();
        if (hostDamageHero != null && clientDamageHero != null) {
            Logger.Debug($"Adding DamageHero component to entity: {Object.Host.name}");

            var damageHero = new HostClientPair<DamageHero> {
                Host = hostDamageHero,
                Client = clientDamageHero
            };

            _components[EntityComponentType.DamageHero] = new DamageHeroComponent(
                _netClient,
                Id,
                Object,
                damageHero
            );
            
            addedComponentsString += " DamageHero";
        }
        
        var hostMeshRenderer = Object.Host.GetComponent<MeshRenderer>();
        var clientMeshRenderer = Object.Client.GetComponent<MeshRenderer>();
        if (hostMeshRenderer != null && clientMeshRenderer != null) {
            Logger.Debug($"Adding MeshRenderer component to entity: {Object.Host.name}");

            var meshRenderer = new HostClientPair<MeshRenderer> {
                Host = hostMeshRenderer,
                Client = clientMeshRenderer
            };

            _components[EntityComponentType.MeshRenderer] = new MeshRendererComponent(
                _netClient,
                Id,
                Object,
                meshRenderer
            );
            
            addedComponentsString += " MeshRenderer";
        }

        EntityInitializer.RemoveClientTypes(Object.Client);

        // Instantiate all types defined in the entity registry, which are passed to the constructor
        foreach (var type in types) {
            var component = ComponentFactory.InstantiateByType(type, _netClient, Id, Object);
            if (component == null) {
                Logger.Debug($"Could not instantiate component for type: {type}");
            } else {
                _components[type] = component;
            }
            
            addedComponentsString += $" {type}";
        }

        Logger.Debug(addedComponentsString);
    }


    /// <summary>
    /// Callback method for entering a hooked FSM action.
    /// </summary>
    /// <param name="self">The FSM action instance that was entered.</param>
    private void OnActionEntered(FsmStateAction self) => _role.OnActionEntered(self);

    /// <summary>
    /// Callback method for handling updates.
    /// </summary>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private void OnUpdate() {
        if (Object.Host == null) {
            if (_lastIsActive) {
                // If the host object was active, but now it null (or destroyed in Unity), we can send
                // to the server that the entity can be regarded as inactive
                if (Object.Client == null) {
                    Logger.Info($"Entity ({Id}, {Type}) host and client object is null (or destroyed) and was active");
                } else {
                    Logger.Info($"Entity '{Object.Client.name}' host object is null (or destroyed) and was active");
                }

                _lastIsActive = false;

                // A host leaving the room destroys every original with it. That says nothing about the enemies,
                // and the next host would take them over switched off, so only a destruction inside a loaded
                // room is reported.
                if (_hostScene.isLoaded) {
                    _netClient.UpdateManager.UpdateEntityIsActive(
                        Id,
                        false
                    );
                } else {
                    Logger.Info($"  Room of entity ({Id}, {Type}) was unloaded, not reporting it inactive");
                }
            }

            return;
        }

        _role.Update(Object.Host.activeSelf);
    }

    /// <summary>
    /// Keeps the original switched off while the scene host is not known yet, and remembers that the room wanted
    /// it active, so the scene host can switch it on once known.
    /// </summary>
    private void KeepOriginalOffUntilSceneHostKnown(bool hostObjectActive) {
        if (hostObjectActive && !KeepsOriginalRunning) {
            Logger.Info($"Entity '{Object.Host.name}' host object became active, but scene host is not determined yet, re-disabling for now");
            _originalIsActive = true;
            Object.Host.SetActive(false);
        }
    }

    /// <summary>
    /// Keeps the original switched off while the replica follows the scene host.
    /// </summary>
    private void KeepOriginalOff(bool hostObjectActive) {
        if (hostObjectActive && !KeepsOriginalRunning) {
            Logger.Info($"Entity '{Object.Host.name}' host object became active, re-disabling");
            Object.Host.SetActive(false);
        }
    }

    /// <summary>
    /// Callback method for when the sprite animator plays an animation.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The sprite animator instance.</param>
    /// <param name="clip">The animation clip that was played.</param>
    /// <param name="clipStartTime">The start time of the animation clip.</param>
    /// <param name="overrideFps">The FPS override for the clip.</param>
    private void OnAnimationPlayed(
        On.tk2dSpriteAnimator.orig_Play_tk2dSpriteAnimationClip_float_float orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        if (self == _animator.Client) {
            if (!_allowClientAnimation) {
                Logger.Info($"Entity '{Object.Client.name}' client animator tried playing animation");
            } else {
                // Logger.Info($"Entity '{_object.Client.name}' client animator was allowed to play animation");

                orig(self, clip, clipStartTime, overrideFps);

                _allowClientAnimation = false;
            }

            return;
        }

        orig(self, clip, clipStartTime, overrideFps);

        if (self != _animator.Host) {
            return;
        }
        _role.OnAnimationPlayed(clip);
    }
    

    /// <summary>
    /// Initializes the entity when the client user is the scene host.
    /// </summary>
    public void InitializeHost() {
        // A room can destroy an original before the scene host is known, such as an encounter that removes
        // itself once seen. There is nothing to activate, and the other entities must still be initialized.
        if (Object.Host == null) {
            SwitchRole(host: true);
            _isSceneHostDetermined = true;
            ActivateGameObject.DoActivateGameObject -= OnDoActivateGameObject;
            Logger.Info($"Entity ({Id}, {Type}) has no original object left; nothing to initialize as host");
            return;
        }

        Object.Host.SetActive(_originalIsActive);
        // An active original runs its own initialization states, as in a game without other players.
        _originalInitialized = Object.Host.activeInHierarchy;

        // Also update the last active variable to account for this potential change
        // Otherwise we might trigger the update sending of activity twice
        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        Logger.Info(
            $"Initializing entity '{Object.Host.name}' with active: {_originalIsActive}, sending active: {_lastIsActive}");

        _netClient.UpdateManager.UpdateEntityIsActive(Id, _lastIsActive);

        SwitchRole(host: true);
        _isSceneHostDetermined = true;

        foreach (var component in _components.Values) {
            component.IsControlled = false;
            component.InitializeHost();
        }
        
        // Deregister the hook for updating the active value of the host object
        ActivateGameObject.DoActivateGameObject -= OnDoActivateGameObject;
    }
    /// <summary>
    /// Initializes the entity when the client user is a scene client. Only sets a variable to indicate the scene
    /// host has been determined.
    /// </summary>
    public void InitializeClient() {
        _isSceneHostDetermined = true;
        
        // Deregister the hook for updating the active value of the host object
        ActivateGameObject.DoActivateGameObject -= OnDoActivateGameObject;

        SwitchRole(IsHost);
    }

    /// <summary>
    /// Enters the host role, or the role a client has: the replica once the scene host is known, and waiting
    /// before that.
    /// </summary>
    private void SwitchRole(bool host) {
        _role = host ? _host : _isSceneHostDetermined ? (Role)_replica : _undetermined;

        // The original stays off only while the replica follows another player's room.
        if (Object?.Host == null) {
            return;
        }

        if (_role == _replica && !KeepsOriginalRunning) {
            ReplicaOriginals.Add(Object.Host);
        } else {
            ReplicaOriginals.Remove(Object.Host);
        }
    }
    /// <summary>
    /// Destroys the entity.
    /// </summary>
    public void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        ReplicaOriginals.Remove(Object.Host);
        RegisteredEntities.Remove(Object.Host);
        RegisteredEntities.Remove(Object.Client);
        On.tk2dSpriteAnimator.Play_tk2dSpriteAnimationClip_float_float -= OnAnimationPlayed;
        On.ObjectPool.Recycle_GameObject -= ObjectPoolOnRecycleGameObject;

        foreach (var component in _components.Values.Distinct()) {
            component.Destroy();
        }
    }

    /// <summary>
    /// Get the list of client FSMs.
    /// </summary>
    /// <returns>A list containing the client FSM instances.</returns>
    public List<PlayMakerFSM> GetClientFsms() {
        return _fsms.Client;
    }
}
