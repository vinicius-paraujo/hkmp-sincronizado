using Hkmp.Networking;

namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// What the server knows of a player that asks to enter or to take its session back.
/// </summary>
/// <param name="ProtocolVersion">The version of the network protocol the client speaks.</param>
/// <param name="Identity">The key of the player.</param>
/// <param name="Username">The name the player asks for.</param>
/// <param name="Address">The address the connection comes from, which only the ban by address looks at.</param>
internal sealed record AdmissionRequest(ushort ProtocolVersion, string Identity, string Username, string Address);

/// <summary>
/// The decision on one request. A refusal always carries the reason the player is shown.
/// </summary>
/// <param name="Accepted">Whether the player may enter.</param>
/// <param name="Reason">Why the player was refused; null when accepted.</param>
/// <param name="PromoteFromPreList">Whether the accepted player's key joins the whitelist through its name.</param>
internal sealed record AdmissionDecision(bool Accepted, string Reason, bool PromoteFromPreList)
{
    /// <summary>The player may enter.</summary>
    public static AdmissionDecision Accept { get; } = new(true, null, false);

    /// <summary>
    /// The player may not enter.
    /// </summary>
    /// <param name="reason">The reason the player is shown.</param>
    /// <returns>The decision.</returns>
    public static AdmissionDecision Refuse(string reason)
    {
        return new AdmissionDecision(false, reason, false);
    }
}

/// <summary>
/// The facts of the server the admission policy decides on.
/// </summary>
internal interface IAdmissionRules
{
    /// <summary>Whether the key is banned.</summary>
    /// <param name="identity">The key.</param>
    /// <returns>True if banned.</returns>
    bool IsKeyBanned(string identity);

    /// <summary>Whether the address is banned, as the server owner may decide for any address.</summary>
    /// <param name="address">The address, without port.</param>
    /// <returns>True if banned.</returns>
    bool IsAddressBanned(string address);

    /// <summary>Whether only whitelisted players may enter.</summary>
    bool WhitelistEnabled { get; }

    /// <summary>Whether the key is on the whitelist.</summary>
    /// <param name="identity">The key.</param>
    /// <returns>True if listed.</returns>
    bool IsWhitelisted(string identity);

    /// <summary>Whether the name was put on the whitelist before its player first joined.</summary>
    /// <param name="username">The name.</param>
    /// <returns>True if pre-listed.</returns>
    bool IsPreListed(string username);

    /// <summary>Whether a player in the game uses the name.</summary>
    /// <param name="username">The name.</param>
    /// <returns>True if in use.</returns>
    bool IsNameInUse(string username);

    /// <summary>Whether a player with the key is in the game, in an active or a suspended session.</summary>
    /// <param name="identity">The key.</param>
    /// <returns>True if such a session exists.</returns>
    bool HasLiveSession(string identity);
}

/// <summary>
/// Decides who enters the server and who takes a session back. The address of a connection never identifies a
/// player; it only counts for the ban by address that the server owner sets.
/// </summary>
internal sealed class AdmissionPolicy
{
    /// <summary>The reason shown to a banned player.</summary>
    public const string BannedReason = "Banned from the server";

    /// <summary>The reason shown to a player missing from the whitelist.</summary>
    public const string NotWhitelistedReason = "Not whitelisted on the server";

    /// <summary>The reason shown for a name that is too long or has other characters than letters and digits.</summary>
    public const string InvalidUsernameReason = "Invalid username";

    /// <summary>The longest name a player may use.</summary>
    public const int MaxUsernameLength = 20;

    private readonly IAdmissionRules _rules;

    /// <summary>
    /// Creates the policy.
    /// </summary>
    /// <param name="rules">The facts of the server.</param>
    public AdmissionPolicy(IAdmissionRules rules)
    {
        _rules = rules;
    }

    /// <summary>
    /// The reason shown to a client that speaks another version of the protocol.
    /// </summary>
    /// <param name="clientVersion">The version of the client.</param>
    /// <returns>The reason.</returns>
    public static string IncompatibleVersionReason(ushort clientVersion)
    {
        return $"Incompatible version: the server uses protocol {NetworkProtocol.Version}, "
            + $"this game uses protocol {clientVersion}";
    }

    /// <summary>
    /// Decides on a player that enters without a session.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The decision.</returns>
    public AdmissionDecision DecideEntry(AdmissionRequest request)
    {
        AdmissionDecision common = DecideCommon(request);
        if (common != null)
        {
            return common;
        }

        bool promote = false;
        if (_rules.WhitelistEnabled && !_rules.IsWhitelisted(request.Identity))
        {
            if (!_rules.IsPreListed(request.Username))
            {
                return AdmissionDecision.Refuse(NotWhitelistedReason);
            }

            promote = true;
        }

        if (!IsValidUsername(request.Username))
        {
            return AdmissionDecision.Refuse(InvalidUsernameReason);
        }

        // A second game with the same settings must not take the place of the player already in. A suspended
        // session of the same key was ended before this decision, so a live one here still sends.
        if (_rules.IsNameInUse(request.Username) || _rules.HasLiveSession(request.Identity))
        {
            return AdmissionDecision.Refuse(ConnectionRejection.UsernameInUse);
        }

        return promote ? new AdmissionDecision(true, null, true) : AdmissionDecision.Accept;
    }

    /// <summary>
    /// Decides on a player that presented the token of its live session. Its place is already its own, so only the
    /// version and the bans count, which the owner may have set while the player was away.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The decision.</returns>
    public AdmissionDecision DecideResumption(AdmissionRequest request)
    {
        return DecideCommon(request) ?? AdmissionDecision.Accept;
    }

    private AdmissionDecision DecideCommon(AdmissionRequest request)
    {
        if (request.ProtocolVersion != NetworkProtocol.Version)
        {
            return AdmissionDecision.Refuse(IncompatibleVersionReason(request.ProtocolVersion));
        }

        if (_rules.IsKeyBanned(request.Identity) || _rules.IsAddressBanned(request.Address))
        {
            return AdmissionDecision.Refuse(BannedReason);
        }

        return null;
    }

    private static bool IsValidUsername(string username)
    {
        if (string.IsNullOrEmpty(username) || username.Length > MaxUsernameLength)
        {
            return false;
        }

        foreach (char character in username)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
