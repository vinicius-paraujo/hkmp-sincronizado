using System.Reflection;
using System.Collections.Generic;
using Hkmp.Game.Client.Save;
using Hkmp.Game.Command.Server;
using Hkmp.Game.Server.Save;
using Hkmp.Game.Settings;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Server;
using Hkmp.Ui;
using Modding;

namespace Hkmp.Game.Server;

/// <summary>
/// Specialization of <see cref="ServerManager"/> that adds handlers for the mod specific things.
/// </summary>
internal class ModServerManager : ServerManager {
    /// <summary>
    /// The UiManager instance for registering events for starting and stopping a server.
    /// </summary>
    private readonly UiManager _uiManager;
    
    /// <summary>
    /// The mod settings instance for retrieving the auth key of the local player to set player save data when
    /// hosting a server.
    /// </summary>
    private readonly ModSettings _modSettings;
    
    /// <summary>
    /// The settings command.
    /// </summary>
    private readonly SettingsCommand _settingsCommand;
    
    /// <summary>
    /// Save data that was loaded from selecting a save file. Will be retroactively applied to a server, if one was
    /// requested to be started after selecting a save file.
    /// </summary>
    private ServerSaveData _loadedLocalSaveData;

    /// <summary>
    /// Whether the save chosen in the menu is a new game. The new game's data is only set up after the server
    /// starts, so until then the game still holds whatever save was loaded before.
    /// </summary>
    private bool _newGameChosen;
    
    public ModServerManager(
        NetServer netServer,
        PacketManager packetManager,
        ServerSettings serverSettings,
        UiManager uiManager,
        ModSettings modSettings
    ) : base(netServer, packetManager, serverSettings) {
        _uiManager = uiManager;
        _modSettings = modSettings;
        _settingsCommand = new SettingsCommand(this, InternalServerSettings);
    }

    /// <inheritdoc />
    public override void Initialize() {
        base.Initialize();
        
        // Start addon loading once all mods have finished loading
        ModHooks.FinishedLoadingModsHook += AddonManager.LoadAddons;

        // Register handlers for UI events
        _uiManager.RequestServerStartHostEvent += port => OnRequestServerStartHost(port, _modSettings.FullSynchronisation);
        _uiManager.RequestServerStopHostEvent += Stop;

        // A save without modded data does not call OnLoadLocal, so the data of a save loaded earlier in the same
        // session would otherwise be hosted with it.
        On.GameManager.StartNewGame += (orig, self, permadeathMode, bossRushMode) => {
            _loadedLocalSaveData = null;
            _newGameChosen = true;
            orig(self, permadeathMode, bossRushMode);
        };
        On.GameManager.LoadGame += (orig, self, saveSlot, callback) => {
            _loadedLocalSaveData = null;
            _newGameChosen = false;
            orig(self, saveSlot, callback);
        };

        // Register application quit handler
        ModHooks.ApplicationQuitHook += Stop;
    }

    /// <summary>
    /// Callback method for when the UI requests the server to be started as a host.
    /// </summary>
    /// <param name="port">The port to start the server on.</param>
    /// <param name="fullSynchronisation">Whether full synchronisation is enabled.</param>
    private void OnRequestServerStartHost(int port, bool fullSynchronisation) {
        // The server outlives each hosted session; a new one must not hand the previous session's data to anyone.
        ServerSaveData.GlobalSaveData = new Dictionary<ushort, byte[]>();
        ServerSaveData.PlayerSaveData = new Dictionary<string, Dictionary<ushort, byte[]>>();

        if (fullSynchronisation) {
            if (_newGameChosen) {
                // Set up the new game's data now, as starting it would a moment later, so the server is seeded
                // with a new game instead of the save that was loaded before.
                // Not public in the patched assembly; the game calls it itself when the new game starts.
                typeof(PlayerData)
                    .GetMethod("SetupNewPlayerData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                    .Invoke(PlayerData.instance, null);
                SceneData.instance.Reset();
            }

            // Get the global save data from the save manager, which obtains the global save data from the loaded
            // save file that the user selected
            ServerSaveData.GlobalSaveData = SaveManager.GetCurrentSaveData(true);

            // Then we import the player save data from the (potentially) loaded modded save file from the user selected
            // save file
            if (_loadedLocalSaveData != null) {
                ServerSaveData.PlayerSaveData = _loadedLocalSaveData.PlayerSaveData;
            }

            // Lastly, we get the player save data from the save manager, which obtains the player save data from the
            // loaded save file that the user selected. We add this data to the server save as the local player
            ServerSaveData.PlayerSaveData[_modSettings.AuthKey] = SaveManager.GetCurrentSaveData(false);
        }

        Start(port, fullSynchronisation);
    }

    /// <inheritdoc />
    protected override void RegisterCommands() {
        base.RegisterCommands();

        CommandManager.RegisterCommand(_settingsCommand);
    }

    /// <inheritdoc />
    protected override void DeregisterCommands() {
        base.DeregisterCommands();
        
        CommandManager.DeregisterCommand(_settingsCommand);
    }

    /// <summary>
    /// Callback for when a local save is loaded.
    /// </summary>
    /// <param name="modSaveFile">The deserialized ModSaveFile instance.</param>
    public void OnLoadLocal(ModSaveFile modSaveFile) {
        _loadedLocalSaveData = modSaveFile.ToServerSaveData();
    }

    /// <summary>
    /// Callback for when a local save is saved.
    /// </summary>
    /// <returns>The ModSaveFile instance to serialize to file.</returns>
    public ModSaveFile OnSaveLocal() {
        return ModSaveFile.FromServerSaveData(ServerSaveData);
    }
}
