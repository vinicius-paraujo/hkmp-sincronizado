using System.Collections.Generic;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// The states of a peer, one per endpoint that passed the cookie check.
/// </summary>
internal enum PeerState
{
    /// <summary>The handshake is in progress.</summary>
    Handshake,

    /// <summary>The handshake finished and the peer exchanges encrypted data.</summary>
    Established,

    /// <summary>The peer ended; it receives nothing more.</summary>
    Closed,
}

/// <summary>
/// The events a peer reacts to.
/// </summary>
internal enum PeerEvent
{
    /// <summary>A datagram arrived from the peer's endpoint.</summary>
    Datagram,

    /// <summary>A client hello of a new handshake with a valid cookie arrived from the same endpoint.</summary>
    NewClientHello,

    /// <summary>The handshake pool finished the handshake.</summary>
    HandshakeSucceeded,

    /// <summary>The handshake pool reported that the handshake failed.</summary>
    HandshakeFailed,

    /// <summary>The handshake did not finish within its deadline.</summary>
    HandshakeDeadline,

    /// <summary>The server ended the peer.</summary>
    Close,
}

/// <summary>
/// What the owner of a peer does when a transition happens.
/// </summary>
internal enum PeerAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>Hands the datagram to the peer's transport, where the handshake reads it.</summary>
    DeliverToHandshake,

    /// <summary>Marks the peer as established and tells the server.</summary>
    AnnounceEstablished,

    /// <summary>Removes the peer and tells the server why its handshake ended.</summary>
    AnnounceFailure,

    /// <summary>Cancels the handshake in progress and removes the peer.</summary>
    CancelHandshake,

    /// <summary>Decrypts the datagram and hands the data to the server.</summary>
    Decrypt,

    /// <summary>Closes the peer, tells the server it was replaced, and starts a new peer for the endpoint.</summary>
    Replace,

    /// <summary>Closes the encrypted transport and removes the peer.</summary>
    CloseTransport,
}

/// <summary>
/// The next state and the action of one transition.
/// </summary>
/// <param name="Next">The state after the transition.</param>
/// <param name="Action">The action the owner of the peer performs.</param>
internal readonly record struct PeerTransition(PeerState Next, PeerAction Action);

/// <summary>
/// The transition table of a peer. Every pair of state and event that is not listed keeps the state and does
/// nothing, so a late or unexpected event can never move a peer.
/// </summary>
internal static class PeerAutomaton
{
    private static readonly Dictionary<(PeerState, PeerEvent), PeerTransition> Table = new()
    {
        [(PeerState.Handshake, PeerEvent.Datagram)] = new(PeerState.Handshake, PeerAction.DeliverToHandshake),
        [(PeerState.Handshake, PeerEvent.NewClientHello)] = new(PeerState.Handshake, PeerAction.DeliverToHandshake),
        [(PeerState.Handshake, PeerEvent.HandshakeSucceeded)] =
            new(PeerState.Established, PeerAction.AnnounceEstablished),
        [(PeerState.Handshake, PeerEvent.HandshakeFailed)] = new(PeerState.Closed, PeerAction.AnnounceFailure),
        [(PeerState.Handshake, PeerEvent.HandshakeDeadline)] = new(PeerState.Closed, PeerAction.AnnounceFailure),
        [(PeerState.Handshake, PeerEvent.Close)] = new(PeerState.Closed, PeerAction.CancelHandshake),
        [(PeerState.Established, PeerEvent.Datagram)] = new(PeerState.Established, PeerAction.Decrypt),
        [(PeerState.Established, PeerEvent.NewClientHello)] = new(PeerState.Closed, PeerAction.Replace),
        [(PeerState.Established, PeerEvent.Close)] = new(PeerState.Closed, PeerAction.CloseTransport),
    };

    /// <summary>
    /// The transition for the event in the state.
    /// </summary>
    /// <param name="state">The current state.</param>
    /// <param name="peerEvent">The event.</param>
    /// <returns>The next state and the action to perform.</returns>
    public static PeerTransition Next(PeerState state, PeerEvent peerEvent)
    {
        return Table.TryGetValue((state, peerEvent), out PeerTransition transition)
            ? transition
            : new PeerTransition(state, PeerAction.None);
    }
}
