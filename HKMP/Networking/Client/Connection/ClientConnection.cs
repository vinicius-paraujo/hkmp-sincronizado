using System;
using System.Net.Sockets;
using Hkmp.Api.Client;
using Hkmp.Networking.Packet.Data;
using Hkmp.Networking.Timing;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Client.Connection;

/// <summary>
/// What the connection needs from the transport, so the rules can be tested without sockets.
/// </summary>
internal interface IClientConnectionPort
{
    /// <summary>
    /// Closes the live connection, if any, and starts a handshake on a new one.
    /// </summary>
    /// <param name="succeeded">Runs on the main thread when the handshake ended well.</param>
    /// <param name="failed">Runs on the main thread with the exception when it did not.</param>
    /// <returns>The running attempt.</returns>
    ConnectAttempt BeginHandshake(Action succeeded, Action<Exception> failed);

    /// <summary>
    /// Sends the client's data, with the token of the session to take back, or an empty one.
    /// </summary>
    /// <param name="sessionToken">The token.</param>
    void SendClientInfo(byte[] sessionToken);

    /// <summary>
    /// Tells the server the player leaves.
    /// </summary>
    void SendLeave();

    /// <summary>
    /// Closes the live connection.
    /// </summary>
    void Close();
}

/// <summary>
/// The state of the client's connection, kept on the main thread and moved only by the transition table of
/// <see cref="ClientConnectionAutomaton"/>. Results from other threads arrive through the mailbox, and the deadlines
/// are checked when it drains, so every event of the connection is raised on the main thread.
/// </summary>
internal sealed class ClientConnection
{
    private readonly IClientConnectionPort _port;
    private readonly ConnectionTimeouts _timeouts;
    private readonly ReconnectPolicy _policy;
    private readonly Func<TimeSpan> _clock;

    private ConnectAttempt _attempt;
    private TimeSpan? _admissionDeadline;
    private TimeSpan? _retryAt;
    private TimeSpan? _giveUpAt;
    private ConnectionFailedResult _pendingFailure;

    /// <summary>
    /// Creates the connection over the given port.
    /// </summary>
    /// <param name="port">The transport.</param>
    /// <param name="timeouts">The deadlines.</param>
    /// <param name="policy">When to try again.</param>
    /// <param name="clock">The current time.</param>
    /// <param name="mailbox">The mailbox whose drains check the deadlines.</param>
    public ClientConnection(
        IClientConnectionPort port,
        ConnectionTimeouts timeouts,
        ReconnectPolicy policy,
        Func<TimeSpan> clock,
        MainThreadMailbox mailbox
    )
    {
        _port = port;
        _timeouts = timeouts;
        _policy = policy;
        _clock = clock;
        mailbox.Drained += Tick;
    }

    /// <summary>The current state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>Why the connection is in <see cref="ConnectionState.Failed"/>, or null.</summary>
    public ConnectionFailedResult Failure { get; private set; }

    /// <summary>The token of the session the server gave this client, or an empty one.</summary>
    public byte[] SessionToken { get; private set; } = [];

    /// <summary>How many attempts started since the connection went silent.</summary>
    public int ResumeAttempts { get; private set; }

    /// <summary>The answer the server gave to the last admission.</summary>
    public ServerInfo LastAnswer { get; private set; }

    /// <summary>
    /// Whether the client info went out on the current connection and its answer has not come yet. Any other
    /// transition ends the wait, so an answer that arrives after it belongs to a connection already given up.
    /// </summary>
    public bool AwaitingAnswer { get; private set; }

    /// <summary>
    /// Raised on the main thread after each change of state, with the action that came with it.
    /// </summary>
    public event Action<ConnectionState, ClientConnectionTransition> Transitioned;

    /// <summary>
    /// Asks for a connection. A failed connection is left first, as the player does by going back to the menu.
    /// </summary>
    /// <param name="keepSession">Whether to present the token of the last session, to take it back.</param>
    public void Connect(bool keepSession)
    {
        if (State == ConnectionState.Failed)
        {
            Fire(ClientConnectionEvent.Leave);
        }

        if (!keepSession)
        {
            SessionToken = [];
        }

        Fire(ClientConnectionEvent.Connect);
    }

    /// <summary>The player leaves.</summary>
    public void Leave() => Fire(ClientConnectionEvent.Leave);

    /// <summary>No packet arrived for the silence deadline.</summary>
    public void OnSilence() => Fire(ClientConnectionEvent.Silence);

    /// <summary>
    /// A packet arrived after the silence. It only means the old connection answered while no attempt replaced it; once
    /// one did, the old connection is closed and a packet can only come through the new one, which still waits for the
    /// server's answer to the admission.
    /// </summary>
    public void OnPacketAfterSilence()
    {
        if (ResumeAttempts == 0)
        {
            Fire(ClientConnectionEvent.Packet);
        }
    }

    /// <summary>
    /// The server ended the session.
    /// </summary>
    /// <param name="message">What the server said, shown to the player.</param>
    public void OnEnded(string message)
    {
        _pendingFailure = new ConnectionFailedMessageResult
        {
            Reason = ConnectionFailedReason.Other,
            Message = message,
        };
        Fire(ClientConnectionEvent.Ended);
    }

    /// <summary>
    /// The server answered the admission.
    /// </summary>
    /// <param name="answer">The answer.</param>
    public void OnAnswer(ServerInfo answer)
    {
        if (!AwaitingAnswer)
        {
            return;
        }

        LastAnswer = answer;
        if (answer.ConnectionResult != ServerConnectionResult.Accepted)
        {
            _pendingFailure = RefusalOf(answer);
            Fire(ClientConnectionEvent.Refused);
            return;
        }

        // A player that presented no token is always admitted as new, so only an answer to a token can say whether
        // the session was taken back.
        bool presentedToken = SessionToken.Length > 0;
        SessionToken = answer.SessionToken ?? [];
        Fire(answer.Resumed || !presentedToken ? ClientConnectionEvent.Accepted : ClientConnectionEvent.AcceptedAsNew);
    }

    /// <summary>
    /// Fires the deadlines that passed. Runs after every drain of the mailbox.
    /// </summary>
    public void Tick()
    {
        TimeSpan now = _clock();
        if (_admissionDeadline is { } admission && now >= admission)
        {
            _admissionDeadline = null;
            _pendingFailure = new ConnectionFailedResult { Reason = ConnectionFailedReason.TimedOut };
            Fire(ClientConnectionEvent.AdmissionDeadline);
        }

        if (_giveUpAt is { } giveUp && now >= giveUp)
        {
            _giveUpAt = null;
            _pendingFailure = new ConnectionFailedResult { Reason = ConnectionFailedReason.TimedOut };
            Fire(ClientConnectionEvent.GiveUp);
        }

        if (_retryAt is { } retry && now >= retry)
        {
            _retryAt = null;
            Fire(ClientConnectionEvent.RetryDue);
        }
    }

    private void Fire(ClientConnectionEvent connectionEvent)
    {
        ClientConnectionTransition transition = ClientConnectionAutomaton.Next(State, connectionEvent);
        if (transition.Action == ClientConnectionAction.None)
        {
            return;
        }

        ConnectionState previous = State;
        State = transition.Next;
        Perform(transition.Action);
        Transitioned?.Invoke(previous, transition);
    }

    private void Perform(ClientConnectionAction action)
    {
        AwaitingAnswer = action == ClientConnectionAction.SendClientInfo;
        switch (action)
        {
            case ClientConnectionAction.StartAttempt:
                StartAttempt();
                break;
            case ClientConnectionAction.RecordFailure:
                _attempt = null;
                RecordFailure();
                break;
            case ClientConnectionAction.CancelAttempt:
                CancelAttempt();
                break;
            case ClientConnectionAction.SendClientInfo:
                _attempt = null;
                _port.SendClientInfo(SessionToken);
                _admissionDeadline = _clock() + _timeouts.Admission;
                break;
            case ClientConnectionAction.Admit:
                _admissionDeadline = null;
                Failure = null;
                break;
            case ClientConnectionAction.CloseAndRecordFailure:
                _admissionDeadline = null;
                _port.Close();
                RecordFailure();
                break;
            case ClientConnectionAction.Close:
                _admissionDeadline = null;
                _port.Close();
                break;
            case ClientConnectionAction.SendLeaveAndClose:
                _port.SendLeave();
                _port.Close();
                SessionToken = [];
                break;
            case ClientConnectionAction.BeginResuming:
                ResumeAttempts = 0;
                _retryAt = _clock() + _policy.NextDelay(0);
                _giveUpAt = _clock() + _timeouts.GiveUp;
                break;
            case ClientConnectionAction.EndResumingOnOldConnection:
                _retryAt = null;
                _giveUpAt = null;
                break;
            case ClientConnectionAction.ScheduleRetry:
                _attempt = null;
                _admissionDeadline = null;
                _retryAt = _clock() + _policy.NextDelay(ResumeAttempts);
                break;
            case ClientConnectionAction.Resume:
            case ClientConnectionAction.Reenter:
                _admissionDeadline = null;
                _giveUpAt = null;
                break;
            case ClientConnectionAction.CancelAndRecordFailure:
                CancelAttempt();
                _port.Close();
                RecordFailure();
                break;
            case ClientConnectionAction.CancelAndClose:
                CancelAttempt();
                _port.Close();
                break;
            case ClientConnectionAction.Publish:
                Failure = null;
                break;
        }
    }

    private void StartAttempt()
    {
        if (State == ConnectionState.Resuming)
        {
            ResumeAttempts++;
        }

        _attempt = _port.BeginHandshake(
            () => Fire(ClientConnectionEvent.HandshakeSucceeded),
            error =>
            {
                _pendingFailure = new ConnectionFailedResult { Reason = ReasonOf(error) };
                Fire(ClientConnectionEvent.HandshakeFailed);
            }
        );
    }

    private void CancelAttempt()
    {
        _attempt?.Cancel();
        _attempt = null;
        _admissionDeadline = null;
        _retryAt = null;
        _giveUpAt = null;
    }

    private void RecordFailure()
    {
        Failure = _pendingFailure ?? new ConnectionFailedResult { Reason = ConnectionFailedReason.Other };
        _pendingFailure = null;
        SessionToken = [];
    }

    private static ConnectionFailedReason ReasonOf(Exception error)
    {
        return error switch
        {
            TlsTimeoutException => ConnectionFailedReason.TimedOut,
            SocketException => ConnectionFailedReason.SocketException,
            _ => ConnectionFailedReason.IOException,
        };
    }

    private static ConnectionFailedResult RefusalOf(ServerInfo answer)
    {
        if (answer.ConnectionResult == ServerConnectionResult.InvalidAddons)
        {
            return new ConnectionInvalidAddonsResult
            {
                Reason = ConnectionFailedReason.InvalidAddons,
                AddonData = answer.AddonData,
            };
        }

        return new ConnectionFailedMessageResult
        {
            Reason = ConnectionFailedReason.Other,
            Message = answer.ConnectionRejectedMessage,
        };
    }
}
