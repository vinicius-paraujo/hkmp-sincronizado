namespace Hkmp.Game.Client.Entity;

/// <summary>
/// Chooses whether the original object is switched on when this client becomes its scene host.
/// </summary>
internal static class HandoverActivity {
    /// <summary>
    /// The previous host's last report is the truth, since the replica can be switched off on this copy alone, for
    /// example by an event of the host that was leaving. The replica's own state is used only when no report arrived.
    /// A replica dies only when the host announced the death, so a dead replica keeps the original off, even if the
    /// host left before reporting it inactive.
    /// </summary>
    /// <param name="keepsOriginalRunning">Whether the original ran all along, as for objects that are never replaced.</param>
    /// <param name="originalActive">Whether the original is switched on now.</param>
    /// <param name="reportedShown">Whether the previous host last showed the object, or null without a report.</param>
    /// <param name="reportedAlive">Whether the previous host last had the object alive, or null without a report.</param>
    /// <param name="replicaActive">Whether the replica is switched on.</param>
    /// <param name="replicaDead">Whether the replica died by the host's announcement.</param>
    public static bool OriginalActive(bool keepsOriginalRunning, bool originalActive, bool? reportedShown,
        bool? reportedAlive, bool replicaActive, bool replicaDead) {
        if (replicaDead) return false;
        if (keepsOriginalRunning) return originalActive;
        if (reportedShown.HasValue && reportedAlive.HasValue) return reportedShown.Value && reportedAlive.Value;
        return replicaActive;
    }
}
