using System.Collections.Generic;
using Hkmp.Networking.Packet;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Everything the state machines of an entity can set on its body: whether it is shown, alive and still carried by
/// another entity, its collider, renderer, animation, physics, invincibility, the damage it deals, and which of its
/// own children are switched on.
/// </summary>
/// <remarks>
/// The scene host otherwise sends only changes, so whatever happened before a player arrived, before the room had
/// a scene host, or in a message that was lost never reaches the other players. A new scene host also resumes the
/// machines in their current state only, so what earlier states did to the body would be lost. This state is sent
/// whole, on every change and again at intervals, and each copy replaces the last; replicas are drawn from it and a
/// new scene host rebuilds the original from it.
/// </remarks>
internal sealed class BodyState {
    private const byte ShownFlag = 1, AliveFlag = 2, CarriedFlag = 4, ColliderFlag = 8, RendererFlag = 16,
        BodyFlag = 32, KinematicFlag = 64, InvincibleFlag = 128;

    private const byte DamageFlag = 1;

    public bool Shown { get; private set; }
    public bool Alive { get; private set; }
    public bool Carried { get; private set; }
    public bool Collider { get; private set; }
    public bool Renderer { get; private set; }
    public string Clip { get; private set; } = "";

    /// <summary>Whether the original has a physics body, so <see cref="Gravity"/> and <see cref="Kinematic"/> describe it.</summary>
    public bool HasBody { get; private set; }

    /// <summary>
    /// The body's gravity. States such as a leap switch it on and a later state switches it off; an original that did
    /// not run those states keeps whatever it had, and without its collider it falls out of the room when taken over.
    /// </summary>
    public float Gravity { get; private set; }

    /// <summary>
    /// Whether physics leaves the body alone. A waiting enemy keeps still and a fight can make a boss dynamic; an
    /// original that did not run those states would otherwise stand still or fall through the floor.
    /// </summary>
    public bool Kinematic { get; private set; }

    /// <summary>Whether the body ignores hits, as a boss does between phases.</summary>
    public bool Invincible { get; private set; }

    /// <summary>Whether the body deals damage on contact, so <see cref="Damage"/> describes it.</summary>
    public bool HasDamage { get; private set; }

    /// <summary>The damage the body deals on contact, which states switch off while it waits or hides.</summary>
    public int Damage { get; private set; }

    /// <summary>
    /// Which children of the body that are not entities of their own are switched on, by name. States switch on
    /// children such as hitboxes once and never again, so a resumed machine does not switch them back on.
    /// </summary>
    public List<BodyChildren.Node> Children { get; private set; } = new List<BodyChildren.Node>();

    /// <summary>Whether a replica drawn from this state should be visible.</summary>
    public bool Visible => Shown && Alive && !Carried;

    /// <summary>Reads the state of a body: the original on the scene host, or the replica when nothing arrived.</summary>
    /// <param name="body">The object to read.</param>
    /// <param name="carried">Whether another entity still holds it, such as a summon before releasing it.</param>
    public static BodyState Of(GameObject body, bool carried) {
        var health = body.GetComponent<HealthManager>();
        var collider = body.GetComponent<Collider2D>();
        var renderer = body.GetComponent<MeshRenderer>();
        var animator = body.GetComponent<tk2dSpriteAnimator>();
        var rigidbody = body.GetComponent<Rigidbody2D>();
        var damage = body.GetComponent<DamageHero>();
        return new BodyState {
            HasBody = rigidbody != null,
            Gravity = rigidbody != null ? rigidbody.gravityScale : 0f,
            Kinematic = rigidbody != null && rigidbody.isKinematic,
            Shown = body.activeInHierarchy,
            Alive = health == null || !health.GetIsDead(),
            Invincible = health != null && health.IsInvincible,
            Carried = carried,
            Collider = collider == null || collider.enabled,
            Renderer = renderer == null || renderer.enabled,
            Clip = animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : "",
            HasDamage = damage != null,
            Damage = damage != null ? damage.damageDealt : 0,
            Children = BodyChildren.Read(body),
        };
    }

    /// <summary>
    /// Sets on the original what the machines can set on a body and do not set again when resumed: the collider,
    /// renderer, physics, invincibility, contact damage and children. Being shown and the animation are applied by
    /// the handover itself, which decides them together with the replica.
    /// </summary>
    /// <param name="original">The original object this client now runs.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public void ApplyTo(GameObject original, string entity) {
        var collider = original.GetComponent<Collider2D>();
        if (collider != null) collider.enabled = Collider;
        var renderer = original.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = Renderer;

        var rigidbody = original.GetComponent<Rigidbody2D>();
        if (rigidbody != null && HasBody) {
            rigidbody.isKinematic = Kinematic;
            rigidbody.gravityScale = Gravity;
        }

        var health = original.GetComponent<HealthManager>();
        if (health != null) health.IsInvincible = Invincible;

        var damage = original.GetComponent<DamageHero>();
        if (damage != null && HasDamage) damage.damageDealt = Damage;

        Logger.Info($"  Body of entity {entity} set to: {this}");
    }

    /// <summary>
    /// Switches the original's own children on or off as listed. Applied after the machines resume, since entering
    /// a state can switch children too.
    /// </summary>
    /// <param name="original">The original object this client now runs.</param>
    /// <param name="entity">The entity's description, for the log.</param>
    public void ApplyChildrenTo(GameObject original, string entity) {
        var missing = BodyChildren.Apply(original, Children);
        if (missing > 0) {
            Logger.Warn($"  Entity {entity} lacks {missing} of the {BodyChildren.Count(Children)} own children its body state lists; those were skipped");
        }
    }

    public void Write(Packet packet) {
        byte flags = 0;
        if (Shown) flags |= ShownFlag;
        if (Alive) flags |= AliveFlag;
        if (Carried) flags |= CarriedFlag;
        if (Collider) flags |= ColliderFlag;
        if (Renderer) flags |= RendererFlag;
        if (HasBody) flags |= BodyFlag;
        if (Kinematic) flags |= KinematicFlag;
        if (Invincible) flags |= InvincibleFlag;
        packet.Write(flags);
        packet.Write(HasDamage ? DamageFlag : (byte)0);
        packet.Write(Clip);
        if (HasBody) packet.Write(Gravity);
        if (HasDamage) packet.Write(Damage);

        WriteChildren(packet, Children);
    }

    public static BodyState Read(Packet packet) {
        var flags = packet.ReadByte();
        var more = packet.ReadByte();
        var state = new BodyState {
            Shown = (flags & ShownFlag) != 0,
            Alive = (flags & AliveFlag) != 0,
            Carried = (flags & CarriedFlag) != 0,
            Collider = (flags & ColliderFlag) != 0,
            Renderer = (flags & RendererFlag) != 0,
            HasBody = (flags & BodyFlag) != 0,
            Kinematic = (flags & KinematicFlag) != 0,
            Invincible = (flags & InvincibleFlag) != 0,
            HasDamage = (more & DamageFlag) != 0,
            Clip = packet.ReadString(),
        };
        if (state.HasBody) state.Gravity = packet.ReadFloat();
        if (state.HasDamage) state.Damage = packet.ReadInt();

        state.Children = ReadChildren(packet);
        return state;
    }

    /// <summary>Writes the children depth first: each one's name, whether it is on, and how many children it has.</summary>
    private static void WriteChildren(Packet packet, List<BodyChildren.Node> nodes) {
        packet.Write((ushort)nodes.Count);
        foreach (var node in nodes) {
            packet.Write(node.Name);
            packet.Write(node.Active);
            WriteChildren(packet, node.Children);
        }
    }

    private static List<BodyChildren.Node> ReadChildren(Packet packet) {
        var count = packet.ReadUShort();
        var nodes = new List<BodyChildren.Node>(count);
        for (var i = 0; i < count; i++) {
            nodes.Add(new BodyChildren.Node {
                Name = packet.ReadString(),
                Active = packet.ReadBool(),
                Children = ReadChildren(packet),
            });
        }

        return nodes;
    }

    public bool SameAs(BodyState other) {
        if (other == null || other.Shown != Shown || other.Alive != Alive || other.Carried != Carried
            || other.Collider != Collider || other.Renderer != Renderer || other.Clip != Clip
            || other.HasBody != HasBody || other.Gravity != Gravity || other.Kinematic != Kinematic
            || other.Invincible != Invincible || other.HasDamage != HasDamage || other.Damage != Damage
            || !BodyChildren.Same(other.Children, Children)) {
            return false;
        }

        return true;
    }

    public override string ToString() =>
        $"shown {Shown}, alive {Alive}, carried {Carried}, collider {Collider}, renderer {Renderer}, clip '{Clip}'" +
        (HasBody ? $", gravity {Gravity}, kinematic {Kinematic}" : "") +
        $", invincible {Invincible}" + (HasDamage ? $", damage {Damage}" : "") +
        $", {BodyChildren.Count(Children)} own children";
}
