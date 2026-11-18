using System.Collections.Generic;

namespace Hkmp.Game.Client.Entity.Action;

/// <summary>
/// Decides which actions of a state a new scene host runs when it takes an enemy over in that state. The rule is to
/// run every action except the ones that must not happen twice, so a state that chooses its way out chooses again
/// and a state's own effects are made again.
/// </summary>
/// <remarks>
/// Two facts about an action come before its type. An action repeated every frame is the state's behaviour while it
/// lasts, so it runs. An event that stays on the enemy's own body is the machine choosing its own way out, so it
/// runs; an event sent anywhere else is a notification the previous host already delivered.
/// </remarks>
internal sealed class ReentryRules {
    private readonly IReadOnlyDictionary<string, ReentryCategory> _categories;

    public ReentryRules(IReadOnlyDictionary<string, ReentryCategory> categories) {
        _categories = categories;
    }

    /// <summary>Whether actions of the given category run when a state is entered again.</summary>
    public static bool Runs(ReentryCategory category) {
        switch (category) {
            case ReentryCategory.Decide:
            case ReentryCategory.Wait:
            case ReentryCategory.Detect:
            case ReentryCategory.Move:
            case ReentryCategory.Set:
            case ReentryCategory.Read:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The category of an action, or null for a type the table does not know.</summary>
    /// <param name="typeName">The action's type name.</param>
    /// <param name="everyFrame">Whether the action repeats every frame.</param>
    /// <param name="eventLeavesBody">Whether the action sends an event to an object other than the enemy's body.</param>
    public ReentryCategory? Categorize(string typeName, bool everyFrame, bool eventLeavesBody) {
        if (!_categories.TryGetValue(typeName, out var category)) {
            return null;
        }

        if (category == ReentryCategory.Decide && eventLeavesBody) {
            return ReentryCategory.Notify;
        }

        if (everyFrame && (category == ReentryCategory.Set || category == ReentryCategory.Read
                           || category == ReentryCategory.Accumulate)) {
            return ReentryCategory.Move;
        }

        return category;
    }

    /// <summary>
    /// Whether an action runs on re-entry. An action an addon marks as re-entrant always runs; an action the table
    /// does not know never does, so nothing runs twice without a decision written down for it.
    /// </summary>
    public bool RunsOnReentry(string typeName, bool everyFrame, bool eventLeavesBody, bool markedReentrant) {
        if (markedReentrant) {
            return true;
        }

        var category = Categorize(typeName, everyFrame, eventLeavesBody);
        return category.HasValue && Runs(category.Value);
    }
}
