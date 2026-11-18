using System;
using System.Collections.Generic;
using System.Linq;
using Hkmp.Api.Client;
using Hkmp.Util;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Game.Client.Entity.Action;

/// <summary>
/// Loads the re-entry table and anchors from the embedded file and applies them to PlayMaker states: which actions
/// run when a new scene host enters a state again, and which state it enters.
/// </summary>
internal static class ReentryRegistry {
    private const string FilePath = "Hkmp.Resource.reentry-registry.json";

    private static readonly ReentryRules Rules;
    private static readonly ReentryAnchors Anchors = new ReentryAnchors();

    static ReentryRegistry() {
        var file = FileUtil.LoadObjectFromEmbeddedJson<ReentryFile>(FilePath);
        var categories = new Dictionary<string, ReentryCategory>();
        if (file == null) {
            Logger.Warn("Could not load the re-entry registry; no action will run again at a handover");
        } else {
            foreach (var pair in file.Actions) {
                categories[pair.Key] = (ReentryCategory) Enum.Parse(typeof(ReentryCategory), pair.Value);
            }

            foreach (var anchor in file.Anchors) {
                Anchors.Add(anchor.Type, anchor.Fsm, anchor.State, anchor.Anchor);
            }
        }

        Rules = new ReentryRules(categories);
    }

    /// <summary>The state a new scene host enters for the state the machine was in.</summary>
    public static string StateToEnter(EntityType type, string fsm, string state) =>
        Anchors.StateToEnter(type.ToString(), fsm, state);

    /// <summary>The actions of the state that run again on re-entry, in their original order.</summary>
    public static FsmStateAction[] ActionsToRun(FsmStateAction[] actions) =>
        actions.Where(action => Rules.RunsOnReentry(action.GetType().Name, RepeatsEveryFrame(action),
            SendsEventOffBody(action), IsMarkedReentrant(action))).ToArray();

    /// <summary>Whether an addon marked the action's type to run again on re-entry.</summary>
    private static bool IsMarkedReentrant(FsmStateAction action) =>
        Attribute.IsDefined(action.GetType(), typeof(ReentrantActionAttribute), inherit: true);

    /// <summary>Whether the action repeats every frame, read from its own "everyFrame" field when it has one.</summary>
    private static bool RepeatsEveryFrame(FsmStateAction action) {
        var value = action.GetType().GetField("everyFrame")?.GetValue(action);
        return value is bool flag ? flag : value is FsmBool fsmBool && fsmBool.Value;
    }

    /// <summary>
    /// Whether the action sends an event somewhere other than the enemy's own body. Events to the machine itself, to
    /// machines linked to it, or to the object that owns it stay on the body; everything else leaves it.
    /// </summary>
    private static bool SendsEventOffBody(FsmStateAction action) {
        if (!(action.GetType().GetField("eventTarget")?.GetValue(action) is FsmEventTarget target)) {
            return false;
        }

        switch (target.target) {
            case FsmEventTarget.EventTarget.Self:
            case FsmEventTarget.EventTarget.HostFSM:
            case FsmEventTarget.EventTarget.SubFSMs:
                return false;
            case FsmEventTarget.EventTarget.GameObject:
            case FsmEventTarget.EventTarget.GameObjectFSM:
                return target.gameObject == null || target.gameObject.OwnerOption != OwnerDefaultOption.UseOwner;
            default:
                return true;
        }
    }

    private sealed class ReentryFile {
        [JsonProperty("actions")]
        public Dictionary<string, string> Actions { get; set; } = new Dictionary<string, string>();

        [JsonProperty("anchors")]
        public List<AnchorEntry> Anchors { get; set; } = new List<AnchorEntry>();
    }

    private sealed class AnchorEntry {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("fsm")] public string Fsm { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("anchor")] public string Anchor { get; set; }
    }
}
