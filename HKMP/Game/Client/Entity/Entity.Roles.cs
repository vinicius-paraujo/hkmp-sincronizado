using HutongGames.PlayMaker;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// The roles an entity can have on this client. Each role decides what the entity does every frame and with what
/// the original does; the work itself stays in the entity.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// What this client does with the entity in one role.
    /// </summary>
    private abstract class Role {
        protected readonly Entity Entity;

        protected Role(Entity entity) {
            Entity = entity;
        }

        /// <summary>Runs once a frame while the original exists.</summary>
        /// <param name="hostObjectActive">Whether the original is switched on.</param>
        public abstract void Update(bool hostObjectActive);

        /// <summary>The original entered a hooked action. Only the scene host acts on it.</summary>
        public virtual void OnActionEntered(FsmStateAction action) {
        }

        /// <summary>The original played an animation. Only the scene host acts on it.</summary>
        public virtual void OnAnimationPlayed(tk2dSpriteAnimationClip clip) {
        }
    }

    /// <summary>
    /// The scene host is not known yet. The original stays switched off, and what the room wants of it is kept
    /// until the scene host is known.
    /// </summary>
    private sealed class UndeterminedRole : Role {
        public UndeterminedRole(Entity entity) : base(entity) {
        }

        public override void Update(bool hostObjectActive) =>
            Entity.KeepOriginalOffUntilSceneHostKnown(hostObjectActive);
    }

    /// <summary>
    /// This client is the scene host. The original runs, and what it does is sent to the other players.
    /// </summary>
    private sealed class HostRole : Role {
        public HostRole(Entity entity) : base(entity) {
        }

        public override void Update(bool hostObjectActive) => Entity.PublishFrame();

        public override void OnActionEntered(FsmStateAction action) => Entity.PublishAction(action);

        public override void OnAnimationPlayed(tk2dSpriteAnimationClip clip) => Entity.PublishAnimation(clip);
    }

    /// <summary>
    /// Another player is the scene host. The original stays switched off, and the replica follows what arrives.
    /// </summary>
    private sealed class ReplicaRole : Role {
        public ReplicaRole(Entity entity) : base(entity) {
        }

        public override void Update(bool hostObjectActive) => Entity.KeepOriginalOff(hostObjectActive);
    }
}
