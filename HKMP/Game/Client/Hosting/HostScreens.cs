using System;
using Hkmp.Ui;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Hkmp.Game.Client.Hosting;

/// <summary>
/// The game and its menus, as hosting drives them. The save counts as loaded once the game leaves the title
/// screen for its first scene.
/// </summary>
internal sealed class HostScreens : IHostScreens
{
    private const string TitleScene = "Menu_Title";

    private readonly UiManager _uiManager;

    /// <summary>
    /// Watches the scenes and wraps the menus.
    /// </summary>
    /// <param name="uiManager">The manager of HKMP's menus.</param>
    public HostScreens(UiManager uiManager)
    {
        _uiManager = uiManager;
        UnitySceneManager.activeSceneChanged += (_, next) =>
        {
            if (next.name != TitleScene)
            {
                GameLoaded?.Invoke();
            }
        };
    }

    /// <inheritdoc />
    public event Action GameLoaded;

    /// <inheritdoc />
    public void Finish(HostingFailure reason, bool gameLoaded)
    {
        string key = reason switch
        {
            HostingFailure.PortInUse => "hostPortInUse",
            HostingFailure.PortUnavailable => "hostPortUnavailable",
            HostingFailure.LocalConnectionFailed => "hostLocalFailed",
            HostingFailure.ServerFailed => "hostServerFailed",
            _ => null,
        };

        // A player who left chose where to go; nothing failed to tell.
        if (key == null)
        {
            return;
        }

        _uiManager.ReturnToMultiplayerMenu(key, gameLoaded);
    }
}
