namespace Hkmp.Game.Client;

/// <summary>
/// Keeps the order of the server's decisions about the room this client is in. Reliable data is applied as it
/// arrives, so a resent decision can land after a newer one and would undo it. The server's epochs only grow, so the
/// epoch of a decision tells which one is newer.
/// </summary>
internal sealed class RoleOrder {
    private string _scene;
    private bool _roomApplied;
    private uint _roleEpoch;

    /// <summary>
    /// The client entered a room. Decisions about earlier rooms no longer apply.
    /// </summary>
    /// <param name="scene">The scene of the room.</param>
    public void EnterRoom(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _roomApplied = false;
        _roleEpoch = 0;
    }

    /// <summary>
    /// Whether a description of the room should be applied. It is applied once per visit, because a resent copy
    /// would put players back in the room that already left it, and one about another room is out of date.
    /// </summary>
    /// <param name="scene">The scene the description is about.</param>
    /// <returns>True if the description should be applied.</returns>
    public bool AcceptRoom(string scene) {
        if (scene != _scene || _roomApplied) {
            return false;
        }

        _roomApplied = true;
        return true;
    }

    /// <summary>
    /// Whether a scene host decision about the current room is not older than the ones applied so far. A change of
    /// host always starts a new epoch, so it must be newer; a description of the room may repeat the current epoch.
    /// </summary>
    /// <param name="scene">The scene the decision is about.</param>
    /// <param name="epoch">The epoch of the decision.</param>
    /// <param name="startsEpoch">Whether the decision is a change of host, which starts its epoch.</param>
    /// <returns>True if the decision should be applied.</returns>
    public bool AcceptRole(string scene, uint epoch, bool startsEpoch) {
        if (scene != _scene || (startsEpoch ? epoch <= _roleEpoch : epoch < _roleEpoch)) {
            return false;
        }

        _roleEpoch = epoch;
        return true;
    }

    /// <summary>
    /// Forget the decisions of the previous connection.
    /// </summary>
    public void Reset() {
        _scene = null;
        _roomApplied = false;
        _roleEpoch = 0;
    }
}
