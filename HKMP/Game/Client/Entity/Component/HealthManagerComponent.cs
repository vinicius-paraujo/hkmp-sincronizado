using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using Modding;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the <see cref="HealthManager"/> component of the entity.
internal class HealthManagerComponent : EntityComponent {
    /// <summary>
    /// Host-client pair of health manager components of the entity.
    /// </summary>
    private readonly HostClientPair<HealthManager> _healthManager;

    /// <summary>
    /// Boolean indicating whether the health manager of the client entity is allowed to die.
    /// </summary>
    private bool _allowDeath;

    /// <summary>
    /// The last value for the "invincible" variable of the health manager.
    /// </summary>
    private bool _lastInvincible;

    /// <summary>
    /// The last value for the "invincibleFromDirection" variable of the health manager.
    /// </summary>
    private int _lastInvincibleFromDirection;
    private int _lastHealth = int.MinValue;
    private int? _receivedHealth;

    /// <summary>
    /// The last health the host reported, kept after a handover consumes <see cref="_receivedHealth"/>.
    /// </summary>
    private int? _knownHealth;

    /// <summary>The type of the entity, sent with its death so the server can tell addons which entity died.</summary>
    private readonly EntityType _entityType;

    public HealthManagerComponent(
        NetClient netClient,
        ushort entityId,
        EntityType entityType,
        HostClientPair<GameObject> gameObject,
        HostClientPair<HealthManager> healthManager
    ) : base(netClient, entityId, gameObject) {
        _healthManager = healthManager;
        _entityType = entityType;

        _lastInvincible = healthManager.Host.IsInvincible;
        _lastInvincibleFromDirection = healthManager.Host.InvincibleFromDirection;

        On.HealthManager.Die += HealthManagerOnDie;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Callback method for when the health manager dies.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The health manager instance.</param>
    /// <param name="attackDirection">The direction of the attack that caused the death.</param>
    /// <param name="attackType">The type of attack that caused the death.</param>
    /// <param name="ignoreEvasion">Whether to ignore evasion.</param>
    private void HealthManagerOnDie(
        On.HealthManager.orig_Die orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        bool ignoreEvasion
    ) {
        if (self != _healthManager.Host && self != _healthManager.Client) {
            orig(self, attackDirection, attackType, ignoreEvasion);
            return;
        }

        if (self == _healthManager.Client) {
            if (!_allowDeath) {
                // Local hits on a replica are feedback only. Left at zero health, the replica would refuse every
                // further hit and be inherited as such by a handover, so it keeps the health the host reported.
                Logger.Info("HealthManager Die was called on client entity, keeping it alive with the host's health");
                self.hp = Mathf.Max(1, _knownHealth ?? 1);
            } else {
                Logger.Info("HealthManager Die was called on client entity, but it is allowed death");

                orig(self, attackDirection, attackType, ignoreEvasion);

                _allowDeath = false;
            }

            return;
        }

        Logger.Info("HealthManager Die was called on host entity");

        orig(self, attackDirection, attackType, ignoreEvasion);

        var data = new EntityNetworkData {
            Type = EntityComponentType.Death
        };
        new EntityDeathData {
            EntityType = _entityType,
            EntityName = self.gameObject.name,
            AttackDirection = attackDirection,
            AttackType = (byte) attackType,
            IgnoreEvasion = ignoreEvasion
        }.WriteData(data.Packet);

        SendData(data);
    }

    /// <summary>
    /// Callback method for updates to check whether invincibility changes.
    /// </summary>
    private void OnUpdate() {
        if (IsControlled || _healthManager.Host == null) return;
        PublishHealth();
        var data = new EntityNetworkData {
            Type = EntityComponentType.Invincibility
        };

        var shouldSend = false;
        
        var newInvincible = _healthManager.Host.IsInvincible;
        if (newInvincible != _lastInvincible) {
            _lastInvincible = newInvincible;
            shouldSend = true;
        }
        data.Packet.Write(newInvincible);

        var newInvincibleFromDir = _healthManager.Host.InvincibleFromDirection;
        if (newInvincibleFromDir != _lastInvincibleFromDirection) {
            _lastInvincibleFromDirection = newInvincibleFromDir;
            shouldSend = true;
        }
        data.Packet.Write((byte) newInvincibleFromDir);

        if (shouldSend) {
            SendData(data);
        }
    }
    
    /// <inheritdoc />
    public override void InitializeHost() {
        PublishHealth();
    }

    private void PublishHealth() {
        if (_healthManager.Host == null || _healthManager.Host.hp == _lastHealth) return;
        _lastHealth = _healthManager.Host.hp;
        var data = new EntityNetworkData { Type = EntityComponentType.Health };
        data.Packet.Write(_lastHealth);
        SendData(data);
    }

    internal void RestoreHealth() {
        // Enabling the original object and restoring its FSM can write initial health again.
        // Apply the authoritative snapshot afterwards, without changing the replica's local hit feedback.
        if (_receivedHealth.HasValue && _healthManager.Host != null) {
            _healthManager.Host.hp = _receivedHealth.Value;
            _receivedHealth = null;
        }
        _lastHealth = int.MinValue;
        PublishHealth();

        // An enemy that the previous host left without health but alive, for instance because its death never
        // arrived, would never die and would hold the fight forever. Restoring failed, so it dies here, and as the
        // new host this copy announces the death to everyone.
        var original = _healthManager.Host;
        // Only health the previous host reported counts: some objects start with no health by design.
        if (original != null && original.gameObject.activeInHierarchy && _knownHealth.HasValue && _knownHealth.Value <= 0
            && original.hp <= 0 && !original.GetIsDead()) {
            Logger.Info("Entity was handed over alive without health, killing it");
            original.Die(null, AttackTypes.Generic, true);
        }
    }

    /// <summary>
    /// Leaves the original dead, as the death on the previous scene host made it, without dying a second time.
    /// </summary>
    /// <remarks>
    /// Dying again would repeat what the previous host already did: the corpse, which can release enemies of its own,
    /// the geo and the notifications. The arena's count of enemies left travels in the arena's own snapshot, so it is
    /// only marked as told, which keeps a later death from counting this enemy twice.
    /// </remarks>
    internal void AdoptDeath() {
        var original = _healthManager.Host;
        if (original == null || original.isDead) {
            return;
        }

        original.hp = 0;
        original.isDead = true;
        var damage = original.GetComponent<DamageHero>();
        if (damage != null) {
            damage.damageDealt = 0;
        }

        ReflectionHelper.SetField(original, "notifiedBattleScene", true);
        original.gameObject.SetActive(false);
        Logger.Info($"  Entity {original.name} takes on the death of the previous scene host");
    }

    /// <summary>Whether the original died on this client, as it does for the scene host that killed it.</summary>
    internal bool OriginalDied => _healthManager.Host != null && _healthManager.Host.GetIsDead();

    /// <summary>The original's health and death, for the log.</summary>
    internal string OriginalState => _healthManager.Host == null ? "no original"
        : $"hp {_healthManager.Host.hp}, dead {_healthManager.Host.GetIsDead()}";

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        Logger.Info("Received health manager update");

        if (!IsControlled) {
            // The previous scene host's death of this entity can arrive after this client took it over; it died all
            // the same, so the original takes the death on instead of living on.
            if (data.Type == EntityComponentType.Death) {
                AdoptDeath();
                return;
            }

            Logger.Info("  Entity was not controlled");
            return;
        }

        if (data.Type == EntityComponentType.Health) {
            _receivedHealth = data.Packet.ReadInt();
            _knownHealth = _receivedHealth;
            // The replica follows the host's health, so local hits never accumulate into a local death.
            if (_healthManager.Client != null && _receivedHealth.Value > 0) {
                _healthManager.Client.hp = _receivedHealth.Value;
            }
        } else if (data.Type == EntityComponentType.Death) {
            var death = EntityDeathData.ReadData(data.Packet);

            // Set a boolean to indicate that the client health manager is allowed to execute the Die method
            _allowDeath = true;
            _healthManager.Client.Die(death.AttackDirection, (AttackTypes) death.AttackType, death.IgnoreEvasion);
        } else if (data.Type == EntityComponentType.Invincibility) {
            var newInvincible = data.Packet.ReadBool();
            var newInvincibleFromDir = data.Packet.ReadByte();

            if (_healthManager.Host != null) {
                _healthManager.Host.IsInvincible = newInvincible;
                _healthManager.Host.InvincibleFromDirection = newInvincibleFromDir;
            }

            if (_healthManager.Client != null) {
                _healthManager.Client.IsInvincible = newInvincible;
                _healthManager.Client.InvincibleFromDirection = newInvincibleFromDir;
            }
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
        On.HealthManager.Die -= HealthManagerOnDie;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
