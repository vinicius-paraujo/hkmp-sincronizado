namespace Hkmp.Api.Client;

/// <summary>
/// The states of the local client's connection to the server.
/// </summary>
public enum ConnectionState {
    /// <summary>No connection and no attempt.</summary>
    Disconnected,

    /// <summary>The handshake with the server is under way.</summary>
    Connecting,

    /// <summary>The handshake ended and the client waits for the server to admit it.</summary>
    Admitting,

    /// <summary>The server admitted the client and data flows both ways.</summary>
    Connected,

    /// <summary>The server went silent and the client tries to take its session back, staying in the game.</summary>
    Resuming,

    /// <summary>The connection ended for a reason the player is shown, until the player leaves.</summary>
    Failed
}
