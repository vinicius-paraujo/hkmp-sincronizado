using System;
using System.Collections.Generic;

namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// The outcome of a resumption: what the session's owner has to do and the connection it has to end.
/// </summary>
/// <typeparam name="TConnection">The connection of a session.</typeparam>
/// <param name="Action">The action of the session's transition.</param>
/// <param name="Previous">The connection the session used until now.</param>
internal readonly record struct Resumption<TConnection>(SessionAction Action, TConnection Previous)
    where TConnection : class;

/// <summary>
/// Opens, resumes and ends sessions. A connection only reaches a session of an admitted player through the
/// session's token, never through its address. It runs on the server core, so it needs no locks.
/// </summary>
/// <typeparam name="TConnection">The connection of a session, which carries its channels.</typeparam>
internal sealed class SessionRegistry<TConnection> where TConnection : class
{
    private readonly Dictionary<TConnection, Session<TConnection>> _byConnection = [];
    private readonly Dictionary<SessionToken, Session<TConnection>> _byToken = [];
    private readonly Dictionary<ushort, Session<TConnection>> _byPlayer = [];
    private readonly PlayerIdPool _ids;

    /// <summary>
    /// Creates the registry.
    /// </summary>
    /// <param name="ids">Where the player ids come from.</param>
    public SessionRegistry(PlayerIdPool ids)
    {
        _ids = ids;
    }

    /// <summary>
    /// The sessions that admitted a player and did not end.
    /// </summary>
    public IEnumerable<Session<TConnection>> Players => _byPlayer.Values;

    /// <summary>
    /// Takes the id for a connection that just finished its handshake, before the connection exists.
    /// </summary>
    /// <returns>The id.</returns>
    public ushort TakeId()
    {
        return _ids.Take();
    }

    /// <summary>
    /// Opens the session of a new connection, in <see cref="SessionState.Admitting"/>.
    /// </summary>
    /// <param name="playerId">The id from <see cref="TakeId"/>.</param>
    /// <param name="connection">The connection.</param>
    /// <returns>The session.</returns>
    public Session<TConnection> Open(ushort playerId, TConnection connection)
    {
        Session<TConnection> session = new(playerId, connection);
        _byConnection[connection] = session;
        return session;
    }

    /// <summary>
    /// The session that uses the connection now.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <returns>The session, or null when the connection belongs to none.</returns>
    public Session<TConnection> Of(TConnection connection)
    {
        return _byConnection.TryGetValue(connection, out Session<TConnection> session) ? session : null;
    }

    /// <summary>
    /// The session of an admitted player.
    /// </summary>
    /// <param name="playerId">The id of the player.</param>
    /// <returns>The session, or null.</returns>
    public Session<TConnection> OfPlayer(ushort playerId)
    {
        return _byPlayer.TryGetValue(playerId, out Session<TConnection> session) ? session : null;
    }

    /// <summary>
    /// The live session the token belongs to.
    /// </summary>
    /// <param name="token">The token a new connection presented.</param>
    /// <returns>The session, or null when no live session has the token.</returns>
    public Session<TConnection> Find(SessionToken token)
    {
        return token.IsPresent && _byToken.TryGetValue(token, out Session<TConnection> session) ? session : null;
    }

    /// <summary>
    /// Whether a player with the identity is in the game, in an active or a suspended session.
    /// </summary>
    /// <param name="identity">The key of the player.</param>
    /// <returns>True if such a session exists.</returns>
    public bool HasLiveSession(string identity)
    {
        return OfIdentity(identity) != null;
    }

    /// <summary>
    /// The active or suspended session of the player with the identity, if any.
    /// </summary>
    /// <param name="identity">The key of the player.</param>
    /// <returns>The session, or null.</returns>
    public Session<TConnection> OfIdentity(string identity)
    {
        foreach (Session<TConnection> session in _byPlayer.Values)
        {
            if (string.Equals(session.Identity, identity, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// Admits the player of a session: it becomes active with a new token.
    /// </summary>
    /// <param name="session">The session, which must be admitting.</param>
    /// <param name="identity">The key of the player.</param>
    /// <returns>The action of the transition; <see cref="SessionAction.None"/> if the session was not admitting.</returns>
    public SessionAction Admit(Session<TConnection> session, string identity)
    {
        SessionAction action = session.Handle(SessionEvent.Accepted);
        if (action != SessionAction.Admit)
        {
            return action;
        }

        session.Admit(identity, SessionToken.Create());
        _byToken[session.Token] = session;
        _byPlayer[session.PlayerId] = session;
        return action;
    }

    /// <summary>
    /// Hands the session the arriving connection, whose own session ends without ever having admitted anyone.
    /// The session gets a new token.
    /// </summary>
    /// <param name="session">The live session whose token the arriving connection presented.</param>
    /// <param name="arriving">The session of the arriving connection, which is admitting.</param>
    /// <returns>The action of the transition and the connection the session used until now.</returns>
    public Resumption<TConnection> Resume(Session<TConnection> session, Session<TConnection> arriving)
    {
        SessionAction action = session.Handle(SessionEvent.Resumption);
        if (action is not (SessionAction.SwapConnection or SessionAction.SwapConnectionAndResume))
        {
            return new Resumption<TConnection>(action, null);
        }

        arriving.Handle(SessionEvent.Leave);
        _byConnection.Remove(arriving.Connection);
        _ids.Release(arriving.PlayerId);

        _byToken.Remove(session.Token);
        TConnection previous = session.Swap(arriving.Connection, SessionToken.Create());
        _byConnection.Remove(previous);
        _byConnection[session.Connection] = session;
        _byToken[session.Token] = session;
        return new Resumption<TConnection>(action, previous);
    }

    /// <summary>
    /// Moves the session by the event, and forgets it when it ends.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="sessionEvent">The event.</param>
    /// <returns>The action of the transition.</returns>
    public SessionAction Handle(Session<TConnection> session, SessionEvent sessionEvent)
    {
        SessionAction action = session.Handle(sessionEvent);
        if (session.State == SessionState.Ended)
        {
            Forget(session);
        }

        return action;
    }

    /// <summary>
    /// Ends every session, as the server does when it stops.
    /// </summary>
    public void Clear()
    {
        foreach (Session<TConnection> session in new List<Session<TConnection>>(_byConnection.Values))
        {
            session.Handle(SessionEvent.Leave);
            Forget(session);
        }
    }

    private void Forget(Session<TConnection> session)
    {
        if (_byConnection.TryGetValue(session.Connection, out Session<TConnection> owner) && owner == session)
        {
            _byConnection.Remove(session.Connection);
        }

        if (session.Token.IsPresent)
        {
            _byToken.Remove(session.Token);
        }

        _byPlayer.Remove(session.PlayerId);
        _ids.Release(session.PlayerId);
    }
}
