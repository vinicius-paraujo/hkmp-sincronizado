namespace Hkmp.Game.Client.Entity;

/// <summary>
/// What an empty object reference of a resumed machine should hold, judged by its name.
/// </summary>
internal static class FsmReferenceName {
    /// <summary>
    /// What a reference holds.
    /// </summary>
    public enum Kind {
        /// <summary>Nothing the handover can fill.</summary>
        Unknown,
        /// <summary>The machine's own object.</summary>
        Self,
        /// <summary>The knight the machine chases.</summary>
        Hero
    }

    /// <summary>
    /// Names under which machines keep the knight they chase.
    /// </summary>
    private static readonly string[] HeroNames = { "Hero", "Knight", "Target", "Player", "Hero Obj", "Hero Object" };

    /// <summary>
    /// Names under which machines keep their own object.
    /// </summary>
    private static readonly string[] SelfNames = { "Self", "Owner" };

    /// <summary>
    /// What the reference with the given name holds; machines differ only in the case of these names.
    /// </summary>
    public static Kind Of(string name) =>
        Matches(name, SelfNames) ? Kind.Self : Matches(name, HeroNames) ? Kind.Hero : Kind.Unknown;

    private static bool Matches(string name, string[] names) {
        foreach (var candidate in names) {
            if (string.Equals(name, candidate, System.StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
