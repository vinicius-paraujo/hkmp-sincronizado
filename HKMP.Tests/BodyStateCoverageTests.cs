using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Sincroniza.Barrier.Tests {
    /// <summary>
    /// Keeps the body state closed: every action the re-entry table runs as "Set" is either carried by a field of the
    /// body state, sent by its own message, not about the body, or listed as a known gap. An action added to the table
    /// without a decision here fails the test.
    /// </summary>
    public class BodyStateCoverageTests {
        private const string Field = "field", Message = "own message", NotBody = "not the body", Gap = "gap";

        private static readonly Dictionary<string, string> Coverage = new Dictionary<string, string> {
            // Fields of the body state.
            ["ActivateGameObject"] = Field,        // shown, and own children
            ["ActivateAllChildren"] = Field,       // own children
            ["SetCollider"] = Field,               // collider
            ["SetCircleCollider"] = Field,         // collider
            ["SetPolygonCollider"] = Field,        // collider
            ["SetMeshRenderer"] = Field,           // renderer
            ["SetSpriteRenderer"] = Field,         // renderer
            ["SetVisibility"] = Field,             // renderer
            ["SetGravity2dScale"] = Field,         // gravity
            ["SetIsKinematic2d"] = Field,          // kinematic
            ["SetInvincible"] = Field,             // invincible
            ["PreventInvincibleEffect"] = Field,   // invincible
            ["SetDamageHeroAmount"] = Field,       // damage
            ["Tk2dPlayAnimation"] = Field,         // clip
            ["Tk2dPlayAnimationV2"] = Field,       // clip
            ["Tk2dPlayFrame"] = Field,             // clip
            ["Tk2dStopAnimation"] = Field,         // clip

            // Sent by messages of their own: position and scale updates.
            ["SetPosition"] = Message,
            ["SetScale"] = Message,
            ["FaceObject"] = Message,
            ["FaceDirection"] = Message,
            ["SetWalkerFacing"] = Message,
            ["iTweenMoveTo"] = Message,
            ["iTweenScaleTo"] = Message,

            // Values of the machine, carried by the snapshot, or objects other than the body.
            ["SetBoolValue"] = NotBody,
            ["SetFloatValue"] = NotBody,
            ["SetFloatValueV2"] = NotBody,
            ["SetIntValue"] = NotBody,
            ["SetStringValue"] = NotBody,
            ["SetGameObject"] = NotBody,
            ["SetVector2XY"] = NotBody,
            ["SetVector3Value"] = NotBody,
            ["SetVector3XYZ"] = NotBody,
            ["FloatClamp"] = NotBody,
            ["EaseFloat"] = NotBody,
            ["BoolFlipEveryFrame"] = NotBody,
            ["SetDreamNailConvo"] = NotBody,
            ["SetSpawnJarContents"] = NotBody,
            ["PreBuildTK2DSprites"] = NotBody,
            ["MoveLiftChain"] = NotBody,
            ["StopLiftChain"] = NotBody,

            // Known gaps: set on the body and not carried yet.
            ["SetVelocity2d"] = Gap,
            ["SetVelocityAsAngle"] = Gap,
            ["SetAngularVelocity2d"] = Gap,
            ["FireAtTarget"] = Gap,
            ["FlingObject"] = Gap,
            ["SetRotation"] = Gap,
            ["RotateTo"] = Gap,
            ["FaceAngle"] = Gap,
            ["FaceAngleV2"] = Gap,
            ["iTweenRotateTo"] = Gap,
            ["SetBoxCollider2DSize"] = Gap,
            ["SetBoxCollider2DSizeVector"] = Gap,
            ["SetBoxColliderTrigger"] = Gap,
            ["SetCircleColliderRadius"] = Gap,
            ["SetLayer"] = Gap,
            ["SetTag"] = Gap,
            ["SetRecoilFreeze"] = Gap,
            ["SetRecoilSpeed"] = Gap,
            ["StartWalker"] = Gap,
            ["StopWalker"] = Gap,
        };

        [Fact]
        public void Every_set_action_has_a_decision() {
            var actions = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Production", "reentry-registry.json")))["actions"];
            var undecided = actions.Properties()
                .Where(action => (string)action.Value == "Set" && !Coverage.ContainsKey(action.Name))
                .Select(action => action.Name)
                .ToList();
            Assert.Empty(undecided);
        }
    }
}
