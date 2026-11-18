namespace Hkmp.Game.Client.Entity.Action;

/// <summary>
/// What a state action does, as far as a new scene host is concerned when it enters the state again. Each category
/// either runs on re-entry or not; <see cref="ReentryRules.Runs"/> holds that choice in one place.
/// </summary>
internal enum ReentryCategory {
    /// <summary>Chooses the way out of the state, such as a test or a random event.</summary>
    Decide,
    /// <summary>Counts time or follows an animation and raises the way out; the time starts again.</summary>
    Wait,
    /// <summary>Watches collisions or line of sight and raises the way out.</summary>
    Detect,
    /// <summary>Drives the body while the state lasts and never finishes by itself.</summary>
    Move,
    /// <summary>Sets an absolute value; doing it again gives the same result.</summary>
    Set,
    /// <summary>Reads a value into the machine's variables without changing anything else.</summary>
    Read,
    /// <summary>Changes a value from its current one; the previous host's result is already in the snapshot.</summary>
    Accumulate,
    /// <summary>
    /// Writes one of the machine's own variables. The snapshot already holds what it wrote there, with every later
    /// change, such as an arena's count of enemies left; writing it again would undo those changes.
    /// </summary>
    Restored,
    /// <summary>Creates an object, which already exists or belongs to the previous host.</summary>
    Create,
    /// <summary>Sends an event or value to another object, which the previous host already delivered.</summary>
    Notify,
    /// <summary>Changes player or game data, which the scene host does not own.</summary>
    Global,
    /// <summary>Changes health or death, which the scene host does not own.</summary>
    Health,
    /// <summary>Plays sound, particles, colour or camera effects; doing it again would only repeat them.</summary>
    Cosmetic,
    /// <summary>Changes the hierarchy or destroys objects; done once, by the first initialization.</summary>
    Structure,
    /// <summary>Depends on history a new scene host does not have; the machine enters an anchor state instead.</summary>
    Unsafe,
}
