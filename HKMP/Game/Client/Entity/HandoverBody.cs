namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Chooses whether the body of an original object is kinematic when this client becomes its scene host.
/// </summary>
internal static class HandoverBody {
    /// <summary>
    /// What the previous host reported is what the body was, whether a waiting enemy kept still or a boss made
    /// dynamic by its fight. Without a report, the earlier rule stands: a body that can stand on something is made
    /// dynamic, since the state that would have done so may have run on the previous host only.
    /// </summary>
    /// <param name="reported">The previous host's last report, or null when none arrived.</param>
    /// <param name="current">The body's own value on this client.</param>
    /// <param name="makeDynamic">Whether the earlier rule makes this body dynamic.</param>
    public static bool Kinematic(bool? reported, bool current, bool makeDynamic) =>
        reported ?? (!makeDynamic && current);
}
