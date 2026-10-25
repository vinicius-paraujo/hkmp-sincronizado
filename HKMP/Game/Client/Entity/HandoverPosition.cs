namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Chooses where the original object of an entity is placed when this client becomes its scene host.
/// </summary>
internal static class HandoverPosition {
    /// <summary>
    /// The client object is a dynamic body with gravity that the previous host only corrects when its own
    /// object moves. A boss that waits hidden and still, as in an introduction, sends no position at all, so
    /// its client copy falls out of the room meanwhile. The last position the previous host sent is the
    /// authoritative one; without any, the previous host never moved the object from where the room placed it.
    /// The original object here cannot be trusted either: it may also have been simulated while inactive for AI.
    /// </summary>
    public static T Choose<T>(bool received, T lastReceived, T initial) => received ? lastReceived : initial;
}
