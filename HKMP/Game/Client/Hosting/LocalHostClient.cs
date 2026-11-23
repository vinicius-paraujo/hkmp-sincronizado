using System;
using Hkmp.Networking.Client;

namespace Hkmp.Game.Client.Hosting;

/// <summary>
/// The client of the player who hosts, as hosting drives it. The network client raises every event on the main
/// thread, so hosting reacts in the same frame.
/// </summary>
internal sealed class LocalHostClient : ILocalClient
{
    private readonly ClientManager _client;

    /// <summary>
    /// Wraps the client of this game.
    /// </summary>
    /// <param name="client">The client manager.</param>
    /// <param name="netClient">The network client under it.</param>
    public LocalHostClient(ClientManager client, NetClient netClient)
    {
        _client = client;

        // The client manager has registered the game's handlers when it raises this, on the main thread.
        _client.ConnectEvent += () => Connected?.Invoke();
        netClient.ConnectFailedEvent += _ => Failed?.Invoke();
        netClient.TimeoutEvent += () => Failed?.Invoke();
    }

    /// <inheritdoc />
    public event Action Connected;

    /// <inheritdoc />
    public event Action Failed;

    /// <inheritdoc />
    public void Connect(int port, string username)
    {
        _client.ConnectToOwnServer(port, username);
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        _client.DisconnectFromOwnServer();
    }
}
