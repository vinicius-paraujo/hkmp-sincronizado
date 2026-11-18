using System;

namespace Hkmp.Api.Client;

/// <summary>
/// Marks a state action that an addon added to an enemy's state machine and that must run again when a new scene
/// host takes the enemy over in that state. Such an action re-checks its own condition, so the machine does not
/// stay in a state whose way out the previous host already took.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ReentrantActionAttribute : Attribute {
}
