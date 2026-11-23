using System;
using Hkmp.Api.Command.Server;
using Hkmp.Api.Eventing;
using Hkmp.Api.Server.Networking;

namespace Hkmp.Api.Server;

/// <summary>
/// Server API interface implementation.
/// </summary>
internal class ServerApi : IServerApi {
    /// <inheritdoc/>
    public IServerManager ServerManager { get; }

    /// <inheritdoc/>
    public IServerCommandManager CommandManager { get; }

    /// <inheritdoc/>
    public INetServer NetServer { get; }

    /// <inheritdoc/>
    public IEventAggregator EventAggregator { get; }

    /// <summary>
    /// The thread that runs the server.
    /// </summary>
    private readonly Hkmp.Game.Server.Core.ServerCore _core;

    public ServerApi(
        IServerManager serverManager,
        IServerCommandManager commandManager,
        INetServer netServer,
        IEventAggregator eventAggregator,
        Hkmp.Game.Server.Core.ServerCore core
    ) {
        _core = core;
        ServerManager = serverManager;
        CommandManager = commandManager;
        NetServer = netServer;
        EventAggregator = eventAggregator;
    }

    /// <inheritdoc/>
    public IScheduledTask Schedule(TimeSpan delay, Action action) => _core.Schedule(delay, action);

    /// <inheritdoc/>
    public void Post(Action action) => _core.Post(action);

    /// <inheritdoc/>
    public void RunInBackground(Action work, Action<Exception> onFinished) => _core.RunInBackground(work, onFinished);
}
