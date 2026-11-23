using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Hkmp.NetworkTests.Harness;

/// <summary>
/// A UDP relay between clients and the server that injects network faults. Like a tunnel or a NAT, it gives every
/// client its own upstream socket, so the server sees all clients on one address with different ports.
/// </summary>
internal sealed class FaultProxy : IDisposable {
    // Windows reports an ICMP "port unreachable" as an error on the next receive; the relay must ignore it.
    private const int SioUdpConnReset = -1744830452;

    // Longer than the send interval of the update managers, so the next datagrams of the same side overtake it.
    private const int ReorderHoldMilliseconds = 80;

    private readonly Socket _front;
    private readonly IPEndPoint _server;
    private readonly ConcurrentDictionary<IPEndPoint, Socket> _upstreams = new();
    private readonly ConcurrentDictionary<IPEndPoint, bool> _silenced = new();
    private readonly System.Collections.Concurrent.BlockingCollection<(long Due, Action Send)> _delayed = new();
    private Socket _reusedUpstream;
    private int _turn;
    private int _lost;
    private volatile bool _disposed;

    public int Port { get; }

    /// <summary>
    /// Datagrams larger than this, in either direction, are dropped, as on a path with a smaller MTU.
    /// </summary>
    public int MaxDatagram { get; set; } = int.MaxValue;

    /// <summary>
    /// The largest datagram seen in either direction.
    /// </summary>
    public int Largest { get; private set; }

    public int Dropped { get; private set; }

    /// <summary>
    /// One-way delay added to every datagram, as the distance between players on the internet adds.
    /// </summary>
    public int DelayMilliseconds { get; set; }

    /// <summary>
    /// When above zero, each client is silenced after this many datagrams reached the server, which leaves its
    /// handshake half done, as when a player closes the game or loses the network while connecting.
    /// </summary>
    public int CutClientsAfter { get; set; }

    private readonly ConcurrentDictionary<IPEndPoint, int> _forwarded = new();

    public FaultProxy(int serverPort) {
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);
        _front = NewSocket();
        Port = ((IPEndPoint) _front.LocalEndPoint).Port;
        new Thread(FrontLoop) { IsBackground = true }.Start();
        new Thread(DelayLoop) { IsBackground = true }.Start();
    }

    /// <summary>
    /// Stops relaying for the client in both directions, as if its machine vanished from the network.
    /// </summary>
    public void Silence(IPEndPoint client) => _silenced[client] = true;

    /// <summary>
    /// Relays the client again after <see cref="Silence"/>, as when its network comes back on the same path.
    /// </summary>
    public void Restore(IPEndPoint client) => _silenced.TryRemove(client, out _);

    /// <summary>
    /// When above zero, every datagram whose turn is a multiple of this is lost, in either direction.
    /// </summary>
    public int LoseEvery { get; set; }

    /// <summary>
    /// When above zero, every datagram whose turn is a multiple of this arrives twice.
    /// </summary>
    public int DuplicateEvery { get; set; }

    /// <summary>
    /// When above zero, every datagram whose turn is a multiple of this is held back long enough for the datagrams
    /// after it to overtake it.
    /// </summary>
    public int ReorderEvery { get; set; }

    /// <summary>
    /// How many datagrams the loss of <see cref="LoseEvery"/> took.
    /// </summary>
    public int Lost => _lost;

    /// <summary>
    /// Gives the client a new upstream port mid-session, as a NAT rebinding or an address change does.
    /// </summary>
    public void Rebind(IPEndPoint client) {
        if (_upstreams.TryGetValue(client, out var old)) {
            var replacement = NewSocket();
            _upstreams[client] = replacement;
            StartUpstreamLoop(client, replacement);
            old.Close();
        }
    }

    /// <summary>
    /// Hands the client's upstream port to the next new client, as a NAT reusing a mapping after a restart does.
    /// The old client stops being relayed.
    /// </summary>
    public void ReuseForNextClient(IPEndPoint client) {
        if (_upstreams.TryRemove(client, out var upstream)) {
            _reusedUpstream = upstream;
        }
    }

    private void FrontLoop() {
        var buffer = new byte[65536];
        while (!_disposed) {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try {
                length = _front.ReceiveFrom(buffer, ref from);
            } catch (SocketException) {
                continue;
            } catch (ObjectDisposedException) {
                return;
            }

            var client = (IPEndPoint) from;
            if (_silenced.ContainsKey(client) || Drop(length)) continue;

            var upstream = _upstreams.GetOrAdd(client, CreateUpstream);
            Forward(upstream, buffer, length, _server);

            if (CutClientsAfter > 0 && _forwarded.AddOrUpdate(client, 1, (_, count) => count + 1) >= CutClientsAfter) {
                Silence(client);
            }
        }
    }

    private Socket CreateUpstream(IPEndPoint client) {
        var upstream = Interlocked.Exchange(ref _reusedUpstream, null);
        if (upstream != null) {
            // The loop of a reused socket keeps running; it reads the current owner on every datagram.
            return upstream;
        }

        upstream = NewSocket();
        StartUpstreamLoop(client, upstream);
        return upstream;
    }

    private void StartUpstreamLoop(IPEndPoint client, Socket upstream) {
        new Thread(() => UpstreamLoop(client, upstream)) { IsBackground = true }.Start();
    }

    private void UpstreamLoop(IPEndPoint firstOwner, Socket upstream) {
        var buffer = new byte[65536];
        while (!_disposed) {
            int length;
            try {
                length = upstream.Receive(buffer);
            } catch (SocketException) {
                continue;
            } catch (ObjectDisposedException) {
                return;
            }

            var owner = OwnerOf(upstream) ?? firstOwner;
            if (_silenced.ContainsKey(owner) || Drop(length)) continue;

            Forward(_front, buffer, length, owner);
        }
    }

    /// <summary>
    /// Sends a copy of the datagram after the configured delay, unless its turn loses, doubles or holds it back.
    /// Failures are dropped, as a real relay drops datagrams for a client that is gone or an upstream closed by a
    /// rebind.
    /// </summary>
    private void Forward(Socket socket, byte[] buffer, int length, EndPoint to) {
        var turn = Interlocked.Increment(ref _turn);
        if (IsTurnOf(LoseEvery, turn)) {
            Interlocked.Increment(ref _lost);
            return;
        }

        var copy = new byte[length];
        Buffer.BlockCopy(buffer, 0, copy, 0, length);
        void Send() {
            try {
                socket.SendTo(copy, 0, length, SocketFlags.None, to);
                if (IsTurnOf(DuplicateEvery, turn)) socket.SendTo(copy, 0, length, SocketFlags.None, to);
            } catch (Exception) {
            }
        }

        var delay = DelayMilliseconds + (IsTurnOf(ReorderEvery, turn) ? ReorderHoldMilliseconds : 0);
        if (delay <= 0) {
            Send();
            return;
        }

        if (IsTurnOf(ReorderEvery, turn)) {
            System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => Send());
            return;
        }

        _delayed.Add((System.Diagnostics.Stopwatch.GetTimestamp() + delay * System.Diagnostics.Stopwatch.Frequency / 1000, Send));
    }

    /// <summary>
    /// Sends the delayed datagrams in the order they arrived, each once its delay passed. A link adds the same latency
    /// to datagrams sent one after the other without reordering them; a timer per datagram would let them overtake
    /// each other whenever they fall due in the same tick of the clock.
    /// </summary>
    private void DelayLoop() {
        foreach (var (due, send) in _delayed.GetConsumingEnumerable()) {
            var wait = (due - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (wait > 0) Thread.Sleep((int) wait);
            send();
        }
    }

    private static bool IsTurnOf(int every, int turn) => every > 0 && turn % every == 0;

    private IPEndPoint OwnerOf(Socket upstream) {
        foreach (var pair in _upstreams) {
            if (pair.Value == upstream) return pair.Key;
        }

        return null;
    }

    private bool Drop(int length) {
        if (length > Largest) Largest = length;
        if (length <= MaxDatagram) return false;
        Dropped++;
        return true;
    }

    private static Socket NewSocket() {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    public void Dispose() {
        _disposed = true;
        _delayed.CompleteAdding();
        _front.Close();
        foreach (var upstream in _upstreams.Values) upstream.Close();
        _reusedUpstream?.Close();
    }
}
