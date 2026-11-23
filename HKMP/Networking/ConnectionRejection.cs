namespace Hkmp.Networking;

/// <summary>
/// Rejection reasons the server and the client must agree on. The value is what travels in the login response.
/// </summary>
internal static class ConnectionRejection {
    public const string UsernameInUse = "Username already in use";

    // Every copy of the game on one Windows account reads the same HKMP settings, so they share one key.
    public const string IdentityInUse = "This game's key belongs to a player already in the session";
}
