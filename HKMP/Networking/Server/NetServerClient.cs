using System.Net;
using Hkmp.Networking.Chunk;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Timing;
using Org.BouncyCastle.Tls;

namespace Hkmp.Networking.Server;

/// <summary>
/// One connection of a client to the server, with its channels. A session uses one connection at a time; when a
/// client takes its session back on a new connection, the new one carries the session's player id from then on.
/// </summary>
internal class NetServerClient {
    /// <summary>
    /// The ID of the player this connection belongs to. It changes only when the connection takes over a session.
    /// </summary>
    public ushort Id { get; private set; }

    /// <summary>
    /// Whether the client is registered.
    /// </summary>
    public bool IsRegistered { get; set; }

    /// <summary>
    /// Whether the server accepted the client's connection request. The server manager adds the player at that
    /// moment, before the client is registered.
    /// </summary>
    public bool IsAccepted { get; set; }
    
    /// <summary>
    /// The update manager for the client.
    /// </summary>
    public ServerUpdateManager UpdateManager { get; }
    
    /// <summary>
    /// The chunk sender instance for sending large amounts of data.
    /// </summary>
    public ServerChunkSender ChunkSender { get; }
    /// <summary>
    /// The chunk receiver instance for receiving large amounts of data.
    /// </summary>
    public ServerChunkReceiver ChunkReceiver { get; }
    
    /// <summary>
    /// The connection manager for the client.
    /// </summary>
    public ServerConnectionManager ConnectionManager { get; }

    /// <summary>
    /// The endpoint the connection comes from. It is an attribute of the connection only, and never identifies the
    /// player.
    /// </summary>
    public readonly IPEndPoint EndPoint;

    /// <summary>
    /// Construct the client with the given DTLS transport and endpoint.
    /// </summary>
    /// <param name="dtlsTransport">The underlying DTLS transport.</param>
    /// <param name="packetManager">The packet manager used on the server.</param>
    /// <param name="endPoint">The endpoint.</param>
    /// <param name="id">The player ID the session registry gave the connection.</param>
    /// <param name="timers">Creates the client's timers, which on the server run on its core.</param>
    /// <param name="timeouts">The deadlines of the connection.</param>
    public NetServerClient(
        DtlsTransport dtlsTransport,
        PacketManager packetManager,
        IPEndPoint endPoint,
        ushort id,
        IUpdateTimerFactory timers,
        ConnectionTimeouts timeouts
    ) {
        EndPoint = endPoint;

        Id = id;
        UpdateManager = new ServerUpdateManager(timers, timeouts) {
            DtlsTransport = dtlsTransport
        };
        ChunkSender = new ServerChunkSender(UpdateManager);
        ChunkReceiver = new ServerChunkReceiver(UpdateManager);
        ConnectionManager = new ServerConnectionManager(
            packetManager, ChunkSender, ChunkReceiver, () => Id, timers, timeouts.Admission
        );
    }

    /// <summary>
    /// Makes the connection carry the player ID of the session it takes over.
    /// </summary>
    /// <param name="id">The session's player ID.</param>
    public void TakeOver(ushort id) {
        Id = id;
    }

    /// <summary>
    /// Disconnect the client from the server.
    /// </summary>
    public void Disconnect() {
        UpdateManager.StopUpdates();
        ChunkSender.Stop();
        ConnectionManager.StopAcceptingConnection();
    }
}
