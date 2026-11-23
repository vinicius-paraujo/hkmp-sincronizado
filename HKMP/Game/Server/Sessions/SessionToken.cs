using System;
using System.Security.Cryptography;

namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// The secret a client presents on a new connection to take its session back. It is the only link between a
/// connection and a session, so the address a connection comes from never decides whose session it is.
/// </summary>
internal readonly struct SessionToken : IEquatable<SessionToken>
{
    /// <summary>The number of bytes of a token.</summary>
    public const int Length = 16;

    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    private readonly byte[] _bytes;

    private SessionToken(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>The token of a client that has no session to take back.</summary>
    public static SessionToken None => default;

    /// <summary>Whether this is a token at all, rather than <see cref="None"/>.</summary>
    public bool IsPresent => _bytes != null;

    /// <summary>
    /// A new token that no one can guess.
    /// </summary>
    /// <returns>The token.</returns>
    public static SessionToken Create()
    {
        byte[] bytes = new byte[Length];
        lock (Random)
        {
            Random.GetBytes(bytes);
        }

        return new SessionToken(bytes);
    }

    /// <summary>
    /// The token with the given bytes, as it arrived from the network.
    /// </summary>
    /// <param name="bytes">Exactly <see cref="Length"/> bytes, or none for <see cref="None"/>.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentException">The number of bytes is neither zero nor <see cref="Length"/>.</exception>
    public static SessionToken FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return None;
        }

        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A session token has {Length} bytes, not {bytes.Length}.", nameof(bytes));
        }

        return new SessionToken((byte[]) bytes.Clone());
    }

    /// <summary>
    /// The bytes of the token, to write to the network; empty for <see cref="None"/>.
    /// </summary>
    /// <returns>A copy of the bytes.</returns>
    public byte[] ToBytes()
    {
        return _bytes == null ? [] : (byte[]) _bytes.Clone();
    }

    /// <inheritdoc />
    public bool Equals(SessionToken other)
    {
        if (_bytes == null || other._bytes == null)
        {
            return _bytes == other._bytes;
        }

        // Compares every byte, so the time taken tells nothing about where two tokens differ.
        int difference = 0;
        for (int i = 0; i < Length; i++)
        {
            difference |= _bytes[i] ^ other._bytes[i];
        }

        return difference == 0;
    }

    /// <inheritdoc />
    public override bool Equals(object obj)
    {
        return obj is SessionToken other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return _bytes == null ? 0 : BitConverter.ToInt32(_bytes, 0);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        // Only a prefix reaches the logs, which is enough to follow a session and too little to take it.
        return _bytes == null ? "none" : BitConverter.ToString(_bytes, 0, 2).Replace("-", "").ToLowerInvariant();
    }
}
