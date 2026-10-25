namespace Hkmp.Logging;

/// <summary>
/// Lets an addon receive HKMP's own messages, for example to keep them in a file per game copy;
/// the Modding API log is shared by every copy installed on the same machine.
/// </summary>
public static class LogSinks {
    /// <summary>
    /// Adds a logger that receives every message HKMP writes from now on.
    /// </summary>
    /// <param name="logger">The logger to add.</param>
    public static void Add(ILogger logger) => Logger.AddLogger(logger);
}
