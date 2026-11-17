using System;

namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Decides when the original object of an entity runs the initialization states of its machines again, and keeps
/// what an earlier initialization found.
/// </summary>
/// <remarks>
/// Initialization states run once in the game. Some of them find a child and then detach it from the body, such as
/// the range a hiding boss uses to notice the player. Running such a state a second time finds nothing, because the
/// child is no longer there, and replaces the working reference with nothing: the boss never notices anyone again.
/// </remarks>
internal static class HandoverInitialization {
    /// <summary>
    /// Whether taking the original over must initialize its machines: only when nothing initialized it before, neither
    /// the game, which runs the room from its start on the first scene host, nor an earlier handover on this client.
    /// </summary>
    public static bool ShouldInitialize(bool initialized) => !initialized;

    /// <summary>
    /// The reference to store after a search: what was found, or the reference already stored when the search found
    /// nothing, so a search repeated after the game moved its target cannot erase a reference that still works.
    /// </summary>
    public static T Keep<T>(T found, T current, Func<T, bool> exists) where T : class =>
        found != null && exists(found) ? found : current != null && exists(current) ? current : found;
}
