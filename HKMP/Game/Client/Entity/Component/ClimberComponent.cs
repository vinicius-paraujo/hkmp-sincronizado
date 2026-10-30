using System.Reflection;
using Hkmp.Networking.Client;
using Hkmp.Networking.Packet.Data;
using UnityEngine;

namespace Hkmp.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the climber behaviour of the entity.
internal class ClimberComponent : EntityComponent {
    /// <summary>
    /// The field in which the climber records its position, set from its Start method onwards.
    /// </summary>
    private static readonly FieldInfo PreviousPosition =
        typeof(Climber).GetField("previousPos", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The <see cref="Climber"/> unity component of the entity.
    /// </summary>
    private readonly Climber _climber;

    public ClimberComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        Climber climber
    ) : base(netClient, entityId, gameObject) {
        _climber = climber;
        _climber.enabled = false;
    }

    /// <inheritdoc />
    /// A host transfer only changes this value, and a climber left disabled would freeze on the new host.
    public override bool IsControlled {
        get => base.IsControlled;
        set {
            var wasControlled = base.IsControlled;
            base.IsControlled = value;
            if (_climber == null) {
                return;
            }

            _climber.enabled = !value;
            if (wasControlled && !value) {
                ResumeWalk();
            }
        }
    }

    /// <summary>
    /// The climber only walks in a routine begun in its Start, which Unity runs once and which ended when the object
    /// was deactivated. Before Start has run, the position it records is still zero and Start will begin the routine.
    /// </summary>
    private void ResumeWalk() {
        if (!_climber.isActiveAndEnabled || (Vector2) PreviousPosition.GetValue(_climber) == Vector2.zero) {
            return;
        }

        _climber.StopAllCoroutines();
        _climber.StartCoroutine("Walk");
    }

    /// <inheritdoc />
    public override void InitializeHost() {
        if (_climber != null) {
            _climber.enabled = true;
        }
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
