namespace Hkmp.Game.Server.Sessions;

/// <summary>
/// One player whose connection finished the handshake: its id, its token once admitted, and the connection it
/// currently uses. The connection can change on a resumption; the id never does.
/// </summary>
/// <typeparam name="TConnection">The connection of the session, which carries its channels.</typeparam>
internal sealed class Session<TConnection> where TConnection : class
{
    /// <summary>
    /// Creates the session of a connection that just finished its handshake.
    /// </summary>
    /// <param name="playerId">The id of the player for as long as the session lasts.</param>
    /// <param name="connection">The connection.</param>
    public Session(ushort playerId, TConnection connection)
    {
        PlayerId = playerId;
        Connection = connection;
    }

    /// <summary>The id of the player, the same from the admission to the end of the session.</summary>
    public ushort PlayerId { get; }

    /// <summary>The connection the session uses now.</summary>
    public TConnection Connection { get; private set; }

    /// <summary>The token the client presents to take the session back; none before the admission.</summary>
    public SessionToken Token { get; private set; }

    /// <summary>The key the player entered with, which identifies the player across connections.</summary>
    public string Identity { get; private set; }

    /// <summary>The state of the session in its automaton.</summary>
    public SessionState State { get; private set; } = SessionState.Admitting;

    /// <summary>
    /// Moves the session by the event, as the automaton says.
    /// </summary>
    /// <param name="sessionEvent">The event.</param>
    /// <returns>What the owner of the session has to do.</returns>
    public SessionAction Handle(SessionEvent sessionEvent)
    {
        SessionTransition transition = SessionAutomaton.Next(State, sessionEvent);
        State = transition.Next;
        return transition.Action;
    }

    /// <summary>
    /// Gives the session the identity and the token of its admission.
    /// </summary>
    /// <param name="identity">The key of the player.</param>
    /// <param name="token">The new token.</param>
    public void Admit(string identity, SessionToken token)
    {
        Identity = identity;
        Token = token;
    }

    /// <summary>
    /// Moves the session to a new connection with a new token, so the old token cannot take it again.
    /// </summary>
    /// <param name="connection">The new connection.</param>
    /// <param name="token">The new token.</param>
    /// <returns>The connection the session used until now.</returns>
    public TConnection Swap(TConnection connection, SessionToken token)
    {
        TConnection previous = Connection;
        Connection = connection;
        Token = token;
        return previous;
    }
}
