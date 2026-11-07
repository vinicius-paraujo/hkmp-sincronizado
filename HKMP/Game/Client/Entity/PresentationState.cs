using Hkmp.Networking.Packet;
using UnityEngine;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// How an entity appears on the scene host: whether it is shown, alive, still carried by another entity, whether
/// its body can be hit and seen, and which animation it plays.
/// </summary>
/// <remarks>
/// The scene host otherwise sends only changes, so whatever happened before a player arrived, before the room had
/// a scene host, or in a message that was lost never reaches the other players, and their replica shows something
/// the enemy is not. This state is sent whole, on every change and again at intervals, and each copy replaces the
/// last; replicas are drawn from it and a new scene host rebuilds the original from it.
/// </remarks>
internal sealed class PresentationState {
    private const byte ShownFlag = 1, AliveFlag = 2, CarriedFlag = 4, ColliderFlag = 8, RendererFlag = 16;

    public bool Shown { get; private set; }
    public bool Alive { get; private set; }
    public bool Carried { get; private set; }
    public bool Collider { get; private set; }
    public bool Renderer { get; private set; }
    public string Clip { get; private set; } = "";

    /// <summary>Whether a replica drawn from this state should be visible.</summary>
    public bool Visible => Shown && Alive && !Carried;

    /// <summary>Reads the state of an original running on the scene host.</summary>
    /// <param name="body">The original object.</param>
    /// <param name="carried">Whether another entity still holds it, such as a summon before releasing it.</param>
    public static PresentationState Of(GameObject body, bool carried) {
        var health = body.GetComponent<HealthManager>();
        var collider = body.GetComponent<Collider2D>();
        var renderer = body.GetComponent<MeshRenderer>();
        var animator = body.GetComponent<tk2dSpriteAnimator>();
        return new PresentationState {
            Shown = body.activeInHierarchy,
            Alive = health == null || !health.GetIsDead(),
            Carried = carried,
            Collider = collider == null || collider.enabled,
            Renderer = renderer == null || renderer.enabled,
            Clip = animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : "",
        };
    }

    public void Write(Packet packet) {
        byte flags = 0;
        if (Shown) flags |= ShownFlag;
        if (Alive) flags |= AliveFlag;
        if (Carried) flags |= CarriedFlag;
        if (Collider) flags |= ColliderFlag;
        if (Renderer) flags |= RendererFlag;
        packet.Write(flags);
        packet.Write(Clip);
    }

    public static PresentationState Read(Packet packet) {
        var flags = packet.ReadByte();
        return new PresentationState {
            Shown = (flags & ShownFlag) != 0,
            Alive = (flags & AliveFlag) != 0,
            Carried = (flags & CarriedFlag) != 0,
            Collider = (flags & ColliderFlag) != 0,
            Renderer = (flags & RendererFlag) != 0,
            Clip = packet.ReadString(),
        };
    }

    public bool SameAs(PresentationState other) =>
        other != null && other.Shown == Shown && other.Alive == Alive && other.Carried == Carried
        && other.Collider == Collider && other.Renderer == Renderer && other.Clip == Clip;

    public override string ToString() =>
        $"shown {Shown}, alive {Alive}, carried {Carried}, collider {Collider}, renderer {Renderer}, clip '{Clip}'";
}
