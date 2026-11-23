using System;
using System.Collections.Generic;
using Hkmp.Api.Addon;
using Hkmp.Game.Server;

namespace Hkmp.Networking.Packet.Data;

/// <summary>
/// Packet data for the client info data.
/// </summary>
internal class ClientInfo : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => false;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The version of the network protocol the client speaks. It comes first, so a server can still read it from a
    /// client whose other data it would misread.
    /// </summary>
    public ushort ProtocolVersion { get; set; } = NetworkProtocol.Version;

    /// <summary>
    /// The token of the session the client takes back, or none for a new entry.
    /// </summary>
    public byte[] SessionToken { get; set; } = [];

    /// <summary>
    /// The username of the client.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// The authentication key of the client.
    /// </summary>
    public string AuthKey { get; set; }

    /// <summary>
    /// A list of addon data of the client.
    /// </summary>
    public List<AddonData> AddonData { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(ProtocolVersion);
        WriteToken(packet, SessionToken);
        packet.Write(Username);
        packet.Write(AuthKey);

        AddonData ??= [];

        var addonDataLength = (byte) System.Math.Min(byte.MaxValue, AddonData.Count);

        packet.Write(addonDataLength);

        for (var i = 0; i < addonDataLength; i++) {
            packet.Write(AddonData[i].Identifier);
            packet.Write(AddonData[i].Version);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        ProtocolVersion = packet.ReadUShort();
        if (ProtocolVersion != NetworkProtocol.Version) {
            // The rest may be laid out differently; the version alone is enough to tell the client why it is refused.
            SessionToken = [];
            Username = "";
            AuthKey = "";
            AddonData = [];
            return;
        }

        SessionToken = ReadToken(packet);
        Username = packet.ReadString(ServerManager.UsernameMaxLength);
        AuthKey = packet.ReadString();

        var addonDataLength = packet.ReadByte();

        AddonData = [];

        for (var i = 0; i < addonDataLength; i++) {
            var id = packet.ReadString();
            var version = packet.ReadString();

            if (id.Length > Addon.MaxNameLength || version.Length > Addon.MaxVersionLength) {
                throw new ArgumentException("Identifier or version of addon exceeds max length");
            }

            AddonData.Add(new AddonData(id, version));
        }
    }

    /// <summary>
    /// Writes a session token as its length and its bytes; a missing token is written as length zero.
    /// </summary>
    /// <param name="packet">The packet to write to.</param>
    /// <param name="token">The token's bytes.</param>
    internal static void WriteToken(IPacket packet, byte[] token) {
        token ??= [];
        packet.Write((byte) token.Length);
        foreach (var value in token) {
            packet.Write(value);
        }
    }

    /// <summary>
    /// Reads a session token written by <see cref="WriteToken"/>.
    /// </summary>
    /// <param name="packet">The packet to read from.</param>
    /// <returns>The token's bytes, empty when there is none.</returns>
    internal static byte[] ReadToken(IPacket packet) {
        var length = packet.ReadByte();
        if (length > 64) {
            throw new ArgumentException("Session token is too long");
        }

        var token = new byte[length];
        for (var i = 0; i < length; i++) {
            token[i] = packet.ReadByte();
        }

        return token;
    }
}

/// <summary>
/// Addon data class denoting identifying information about an addon.
/// </summary>
internal class AddonData : IEquatable<AddonData> {
    /// <summary>
    /// The identifier of the addon (aka name).
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The version of the addon.
    /// </summary>
    public string Version { get; }

    public AddonData(string identifier, string version) {
        Identifier = identifier;
        Version = version;
    }

    /// <inheritdoc/>
    public bool Equals(AddonData other) {
        if (ReferenceEquals(null, other)) {
            return false;
        }

        if (ReferenceEquals(this, other)) {
            return true;
        }

        return Identifier == other.Identifier && Version == other.Version;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) {
        if (ReferenceEquals(null, obj)) {
            return false;
        }

        if (ReferenceEquals(this, obj)) {
            return true;
        }

        if (obj.GetType() != GetType()) {
            return false;
        }

        return Equals((AddonData) obj);
    }

    /// <inheritdoc/>
    public override int GetHashCode() {
        unchecked {
            return ((Identifier != null ? Identifier.GetHashCode() : 0) * 397) ^
                   (Version != null ? Version.GetHashCode() : 0);
        }
    }
}
