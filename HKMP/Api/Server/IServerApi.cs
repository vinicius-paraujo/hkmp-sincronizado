using System;
using Hkmp.Api.Command.Server;
using Hkmp.Api.Eventing;
using Hkmp.Api.Server.Networking;

namespace Hkmp.Api.Server;

/// <summary>
/// The server API.
/// </summary>
public interface IServerApi {
    /// <summary>
    /// The interface for the server manager.
    /// </summary>
    IServerManager ServerManager { get; }

    /// <summary>
    /// Command manager for registering server-side commands.
    /// </summary>
    IServerCommandManager CommandManager { get; }

    /// <summary>
    /// The net server for all network-related interaction.
    /// </summary>
    INetServer NetServer { get; }

    /// <summary>
    /// Inter-addon communication event bus.
    /// </summary>
    IEventAggregator EventAggregator { get; }

    /// <summary>
    /// Runs the action on the server thread after the delay. Every handler, event and command of the server runs
    /// on that thread, and the server manager and network senders may only be used from it.
    /// </summary>
    /// <param name="delay">The delay.</param>
    /// <param name="action">The action.</param>
    /// <returns>The deadline, which can be cancelled from any thread.</returns>
    IScheduledTask Schedule(TimeSpan delay, Action action);

    /// <summary>
    /// Runs the action on the server thread as soon as possible. Use it to reach the server from another thread.
    /// </summary>
    /// <param name="action">The action.</param>
    void Post(Action action);

    /// <summary>
    /// Runs slow work, such as disk access, on another thread, so that the server thread never waits for it.
    /// </summary>
    /// <param name="work">The work, which must not use the server manager or the network senders.</param>
    /// <param name="onFinished">Runs on the server thread afterwards, with the exception of the work or null.</param>
    void RunInBackground(Action work, Action<Exception> onFinished);
}

/// <summary>
/// A deadline scheduled on the server thread.
/// </summary>
public interface IScheduledTask {
    /// <summary>
    /// Whether the deadline was cancelled.
    /// </summary>
    bool IsCancelled { get; }

    /// <summary>
    /// Cancels the deadline. A deadline that already ran is not affected.
    /// </summary>
    void Cancel();
}
