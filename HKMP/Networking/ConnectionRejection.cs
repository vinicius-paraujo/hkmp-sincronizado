namespace Hkmp.Networking;

/// <summary>
/// Rejection reasons the server and the client must agree on. The value is what travels in the login response.
/// </summary>
internal static class ConnectionRejection {
    public const string UsernameInUse = "Username already in use";
}
