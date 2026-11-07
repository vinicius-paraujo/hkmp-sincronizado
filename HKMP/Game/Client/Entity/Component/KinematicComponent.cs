using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Util;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component keeps whether the body of an entity is kinematic, as its scene host has it.
/// A player who joins a room as a client never runs the original object's first states, which is where a
/// waiting enemy, such as the Moss Knight hidden in its bush, is made kinematic. Taking the room over later, that
/// original would otherwise start as a falling body and drop through the floor before its fight.
internal class KinematicComponent : EntityComponent {
    /// <summary>
    /// The host <see cref="Rigidbody2D"/> unity component of the entity.
    /// </summary>
    private readonly Rigidbody2D _rigidbody;

    /// <summary>
    /// The value last sent while this client is the scene host; null before the first one.
    /// </summary>
    private bool? _lastKinematic;

    /// <summary>
    /// What the scene host last reported, kept until this client takes the room over; null when nothing came.
    /// </summary>
    public bool? Received { get; private set; }

    public KinematicComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        Rigidbody2D rigidbody
    ) : base(netClient, entityId, gameObject) {
        _rigidbody = rigidbody;

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Callback for checking the body each update.
    /// </summary>
    private void OnUpdate() {
        if (IsControlled || _rigidbody == null) {
            return;
        }

        var kinematic = _rigidbody.isKinematic;
        if (_lastKinematic == kinematic) {
            return;
        }

        _lastKinematic = kinematic;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Kinematic
        };
        data.Packet.Write(kinematic);

        SendData(data);
    }

    /// <summary>
    /// Called once this client became the scene host and the body was set from <see cref="Received"/>, so the
    /// body is reported again from here on, as this client now has it.
    /// </summary>
    public void TakenOver() {
        Received = null;
        _lastKinematic = null;
    }

    /// <inheritdoc />
    public override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        Received = data.Packet.ReadBool();
    }

    /// <inheritdoc />
    public override void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }
}
