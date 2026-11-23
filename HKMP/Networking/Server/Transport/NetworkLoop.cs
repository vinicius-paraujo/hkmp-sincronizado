using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Hkmp.Logging;

namespace Hkmp.Networking.Server.Transport;

/// <summary>
/// The network thread of the server: it reads the socket, routes each datagram and runs the commands other
/// threads post. A failure while handling one datagram is logged and never ends the loop.
/// </summary>
internal sealed class NetworkLoop
{
    /// <summary>
    /// The largest UDP payload, so that no datagram is ever cut by the receive call.
    /// </summary>
    private const int ReceiveBufferSize = 65507;

    /// <summary>
    /// How long the loop waits for a datagram before it runs the posted commands again, in microseconds.
    /// </summary>
    private const int PollMicroseconds = 5000;

    private readonly Socket _socket;
    private readonly PeerDemux _demux;
    private readonly NetworkCommands _commands;
    private readonly byte[] _buffer = new byte[ReceiveBufferSize];

    /// <summary>
    /// Creates the loop.
    /// </summary>
    /// <param name="socket">The bound server socket.</param>
    /// <param name="demux">Routes the datagrams to the peers.</param>
    /// <param name="commands">The inbox of the network thread.</param>
    public NetworkLoop(Socket socket, PeerDemux demux, NetworkCommands commands)
    {
        _socket = socket;
        _demux = demux;
        _commands = commands;
    }

    /// <summary>
    /// Runs until the token is cancelled or the socket is closed.
    /// </summary>
    /// <param name="token">Cancels the loop.</param>
    public void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _commands.RunAll();

            if (!TryReceive(token, out IPEndPoint from, out int length))
            {
                continue;
            }

            try
            {
                _demux.Route(from, _buffer, length);
            }
            catch (Exception e)
            {
                Logger.Error($"Could not handle a datagram from {from}:\n{e}");
            }
        }
    }

    private bool TryReceive(CancellationToken token, out IPEndPoint from, out int length)
    {
        from = null;
        length = 0;
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            if (!_socket.Poll(PollMicroseconds, SelectMode.SelectRead))
            {
                return false;
            }

            length = _socket.ReceiveFrom(_buffer, SocketFlags.None, ref source);
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException e)
        {
            // The failed call delivered no datagram, so there is nothing to route.
            Logger.Debug($"Socket error on the server socket: {e.SocketErrorCode}");
            return false;
        }

        from = (IPEndPoint) source;
        return true;
    }
}
