using System;
using System.Net.Sockets;
using Hkmp.Logging;

namespace Hkmp.Networking.Server.Lifecycle;

/// <summary>
/// Why the server is in the failed state.
/// </summary>
internal enum ServerFailure
{
    /// <summary>The server did not fail.</summary>
    None,

    /// <summary>Another program holds the port.</summary>
    PortInUse,

    /// <summary>The port could not be opened for another reason.</summary>
    PortUnavailable,

    /// <summary>A thread of the server kept failing and was given up.</summary>
    LoopFailed,
}

/// <summary>
/// The socket and the threads of the server, as the lifecycle drives them.
/// </summary>
internal interface IServerPort
{
    /// <summary>
    /// Binds the port and starts the threads.
    /// </summary>
    /// <param name="port">The port.</param>
    /// <exception cref="SocketException">The port could not be bound.</exception>
    void Open(int port);

    /// <summary>
    /// Asks the threads to end and waits for them up to the stop deadline.
    /// </summary>
    /// <returns>False if a thread was still running when the deadline passed.</returns>
    bool Close();
}

/// <summary>
/// Starts and stops the server and tells its real state. It runs on the server's core, which reports the failures
/// of the threads to it.
/// </summary>
internal sealed class ServerLifecycle
{
    private readonly IServerPort _port;
    private readonly Action _endSessions;
    private int _requestedPort;

    /// <summary>
    /// Creates the lifecycle of a stopped server.
    /// </summary>
    /// <param name="port">The socket and the threads of the server.</param>
    /// <param name="endSessions">Tells every session that the server goes away.</param>
    public ServerLifecycle(IServerPort port, Action endSessions)
    {
        _port = port;
        _endSessions = endSessions;
    }

    /// <summary>
    /// Called on the core whenever the state is published, with the reason of a failure.
    /// </summary>
    public event Action<ServerState, ServerFailure> StateChanged;

    /// <summary>The current state.</summary>
    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>Why the server failed; <see cref="ServerFailure.None"/> unless it is in the failed state.</summary>
    public ServerFailure Failure { get; private set; }

    /// <summary>
    /// Starts the server on the port. It does nothing unless the server is stopped.
    /// </summary>
    /// <param name="port">The port.</param>
    public void Start(int port)
    {
        _requestedPort = port;
        Handle(ServerLifecycleEvent.Start);
    }

    /// <summary>
    /// Stops the server from any state but stopped.
    /// </summary>
    public void Stop()
    {
        Handle(ServerLifecycleEvent.Stop);
    }

    /// <summary>
    /// Reports that a thread of the server failed for good.
    /// </summary>
    public void OnLoopFailed()
    {
        Handle(ServerLifecycleEvent.LoopFailed, ServerFailure.LoopFailed);
    }

    // The failure is only taken when the event moves the server, so a late report never marks a stopped one.
    private void Handle(ServerLifecycleEvent lifecycleEvent, ServerFailure failure = ServerFailure.None)
    {
        ServerLifecycleTransition transition = ServerLifecycleAutomaton.Next(State, lifecycleEvent);
        if (transition.Action == ServerLifecycleAction.None)
        {
            return;
        }

        State = transition.Next;
        if (failure != ServerFailure.None)
        {
            Failure = failure;
        }

        Perform(transition.Action);
    }

    private void Perform(ServerLifecycleAction action)
    {
        switch (action)
        {
            case ServerLifecycleAction.OpenPort:
                Failure = ServerFailure.None;
                OpenPort();
                break;
            case ServerLifecycleAction.Publish:
                Publish();
                break;
            case ServerLifecycleAction.RecordFailure:
                Logger.Error($"The server failed on port {_requestedPort}: {Failure}");
                Publish();
                break;
            case ServerLifecycleAction.RecordFailureAndEndSessions:
                Logger.Error($"The server failed on port {_requestedPort}: {Failure}");
                _endSessions();
                Publish();
                break;
            case ServerLifecycleAction.EndThreads:
                EndThreads();
                break;
            case ServerLifecycleAction.EndSessionsAndThreads:
                _endSessions();
                EndThreads();
                break;
            case ServerLifecycleAction.RecordLingeringThreads:
                Logger.Warn("A thread of the server did not end within the stop deadline");
                Publish();
                break;
        }
    }

    private void OpenPort()
    {
        try
        {
            _port.Open(_requestedPort);
        }
        catch (SocketException e)
        {
            Logger.Info($"Port {_requestedPort} could not be opened: {e.Message}");
            Handle(
                ServerLifecycleEvent.PortRefused,
                e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? ServerFailure.PortInUse
                    : ServerFailure.PortUnavailable
            );
            return;
        }

        Handle(ServerLifecycleEvent.PortOpened);
    }

    private void EndThreads()
    {
        bool ended = _port.Close();
        Failure = ServerFailure.None;
        Handle(ended ? ServerLifecycleEvent.Ended : ServerLifecycleEvent.StopDeadline);
    }

    private void Publish()
    {
        StateChanged?.Invoke(State, Failure);
    }
}
