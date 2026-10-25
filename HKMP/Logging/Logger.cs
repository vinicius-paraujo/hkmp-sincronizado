using System.Collections.Generic;

namespace Hkmp.Logging;

internal static class Logger {
    /// <summary>
    /// A list of logger instances
    /// </summary>
    // Replaced, never modified: an addon can add a logger while network threads are logging.
    private static ILogger[] Loggers = [];

    private static readonly object AddLock = new();

    /// <summary>
    /// Log a message as information to all registered loggers.
    /// </summary>
    /// <param name="message">The string message.</param>
    public static void Info(string message) {
        foreach (var logger in Loggers) {
            logger.Info(message);
        }
    }

    /// <summary>
    /// Log a message as fine information to all registered loggers.
    /// </summary>
    /// <param name="message">The string message.</param>
    public static void Fine(string message) {
        foreach (var logger in Loggers) {
            logger.Fine(message);
        }
    }

    /// <summary>
    /// Log a message as debug information to all registered loggers.
    /// </summary>
    /// <param name="message">The string message.</param>
    public static void Debug(string message) {
        foreach (var logger in Loggers) {
            logger.Debug(message);
        }
    }

    /// <summary>
    /// Log a message as a warning to all registered loggers.
    /// </summary>
    /// <param name="message">The string message.</param>
    public static void Warn(string message) {
        foreach (var logger in Loggers) {
            logger.Warn(message);
        }
    }

    /// <summary>
    /// Log a message as an error to all registered loggers.
    /// </summary>
    /// <param name="message">The string message.</param>
    public static void Error(string message) {
        foreach (var logger in Loggers) {
            logger.Error(message);
        }
    }

    /// <summary>
    /// Add a logger instance to use when logging.
    /// </summary>
    /// <param name="logger">The instance of ILogger.</param>
    public static void AddLogger(ILogger logger) {
        lock (AddLock) {
            var loggers = new ILogger[Loggers.Length + 1];
            Loggers.CopyTo(loggers, 0);
            loggers[Loggers.Length] = logger;
            Loggers = loggers;
        }
    }
}
