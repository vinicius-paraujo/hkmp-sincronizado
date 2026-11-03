namespace Hkmp.Game.Client;

/// <summary>
/// Keeps the order of the server's scene host decisions. Reliable data is applied as it arrives, so a resent decision
/// can land after a newer one and would undo it. The server numbers its decisions, which makes an old one easy to spot.
/// </summary>
internal sealed class RoleOrder {
    private uint _latest;
    private uint _latestRoom;

    /// <summary>
    /// Whether the decision with the given number is newer than every decision applied so far.
    /// </summary>
    /// <param name="decision">The number the server gave the decision, starting at one.</param>
    /// <returns>True if the decision should be applied.</returns>
    public bool Accept(uint decision) {
        if (decision <= _latest) {
            return false;
        }

        _latest = decision;
        return true;
    }

    /// <summary>
    /// Whether a description of the room is newer than the last one applied. It is applied at most once, because an
    /// older description would put players back in a room they already left.
    /// </summary>
    /// <param name="decision">The number the server gave the description.</param>
    /// <returns>True if the description should be applied.</returns>
    public bool AcceptRoom(uint decision) {
        if (decision <= _latestRoom) {
            return false;
        }

        _latestRoom = decision;
        return true;
    }

    /// <summary>
    /// Forget the decisions of the previous connection, because the server numbers each connection from the start.
    /// </summary>
    public void Reset() => _latest = _latestRoom = 0;
}
