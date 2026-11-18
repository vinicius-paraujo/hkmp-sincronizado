using System.Collections.Generic;

namespace Hkmp.Game.Client.Entity.Action;

/// <summary>
/// The state a new scene host enters instead of a state it cannot enter again, such as one that returns to whichever
/// state came before it. The anchor is the nearest earlier state that chooses what to do next.
/// </summary>
internal sealed class ReentryAnchors {
    private readonly Dictionary<(string Type, string Fsm, string State), string> _anchors =
        new Dictionary<(string, string, string), string>();

    public void Add(string type, string fsm, string state, string anchor) {
        _anchors[(type, fsm, state)] = anchor;
    }

    /// <summary>The state to enter for the given one: its anchor if it has one, otherwise the state itself.</summary>
    public string StateToEnter(string type, string fsm, string state) {
        return state != null && _anchors.TryGetValue((type, fsm, state), out var anchor) ? anchor : state;
    }
}
