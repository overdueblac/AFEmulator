using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AFConnect.Proxy;

/// <summary>
/// Relays UDP through an isolated upstream socket with a fixed remote endpoint for each client peer,
/// preventing cross-session packet delivery.
/// </summary>
public sealed class UdpForwarder : IAsyncDisposable
{
    private readonly IPEndPoint _local;
    private readonly IPEndPoint _remote;
    private readonly int _maxPeers;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<IPEndPoint, Peer> _peers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycle = new();
    private UdpClient? _listener;
    private Task? _run;
    private bool _disposed;

    /// <summary>
    /// The actual local endpoint after Start binds. Port zero is assigned by the OS; returns null before
    /// startup.
    /// </summary>
    public IPEndPoint? LocalEndpoint => _listener?.Client.LocalEndPoint as IPEndPoint;

    public UdpForwarder(IPEndPoint local, IPEndPoint remote, int maxPeers = 128, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (maxPeers is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxPeers));
        if (idleTimeout is { } timeout && (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromHours(1)))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (remote.Port == 0 || remote.Address.Equals(IPAddress.Any) || remote.Address.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("A concrete UDP destination address and port are required.", nameof(remote));
        _local = new IPEndPoint(local.Address, local.Port);
        _remote = new IPEndPoint(remote.Address, remote.Port);
        _maxPeers = maxPeers;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Binds the local port and returns the complete forwarding loop. Call once only; cancellation waits
    /// for all sessions to finish.
    /// </summary>
    public Task Start(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null) throw new InvalidOperationException("Forwarder already started.");
            _listener = new UdpClient(_local);
            _run = RunAsync(cancellationToken);
            return _run;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Task receive = ReceiveClientsAsync(lifetime.Token);
        Task expire = ExpirePeersAsync(lifetime.Token);

        try
        {
            await Task.WhenAny(receive, expire);
            await lifetime.CancelAsync();
            await Task.WhenAll(receive, expire);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var pair in _peers)
                if (_peers.TryRemove(pair.Key, out var peer)) await peer.DisposeAsync();
            _listener!.Dispose();
        }
    }

    private async Task ReceiveClientsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            UdpReceiveResult packet = await _listener!.ReceiveAsync(cancellationToken);

            if (!_peers.TryGetValue(packet.RemoteEndPoint, out Peer? peer))
            {
                if (_peers.Count >= _maxPeers) continue;
                peer = new Peer(packet.RemoteEndPoint, _remote);
                if (!_peers.TryAdd(packet.RemoteEndPoint, peer))
                {
                    await peer.DisposeAsync();
                    continue;
                }
                peer.Start(_listener, cancellationToken);
            }
            try
            {
                peer.Touch();
                await peer.Upstream.SendAsync(packet.Buffer.AsMemory(), cancellationToken);
            }
            catch (ObjectDisposedException) { /* An idle-cleanup race may safely drop this UDP packet. */ }
            catch (SocketException) { peer.Stop(); }
        }
    }

    private async Task ExpirePeersAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var pair in _peers)
            {
                if (!pair.Value.IsComplete && pair.Value.IdleFor < _idleTimeout) continue;
                if (_peers.TryRemove(pair.Key, out Peer? peer)) await peer.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;

        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            run = _run;
        }
        try { if (run is not null) await run; }
        finally { _stop.Dispose(); }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly IPEndPoint _client;
        private readonly CancellationTokenSource _stop = new();
        private Task? _receive;
        private long _lastSeen = Environment.TickCount64;
        public UdpClient Upstream { get; }
        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastSeen));
        public bool IsComplete => _receive?.IsCompleted == true;

        public Peer(IPEndPoint client, IPEndPoint remote)
        {
            _client = client;
            Upstream = new UdpClient(remote.AddressFamily);
            // Connect restricts accepted sources and gives each local client its own upstream UDP port.
            Upstream.Connect(remote);
        }

        public void Touch() => Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);
        public void Stop()
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { /* Idle cleanup has already disposed this session. */ }
        }
        public void Start(UdpClient listener, CancellationToken cancellationToken) => _receive = ReceiveAsync(listener, cancellationToken);

        private async Task ReceiveAsync(UdpClient listener, CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);

            try
            {
                while (true)
                {
                    UdpReceiveResult packet = await Upstream.ReceiveAsync(lifetime.Token);

                    Touch();
                    await listener.SendAsync(packet.Buffer.AsMemory(), _client, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            // ICMP unreachable terminates only this UDP peer, leaving other players and TCP control connections intact.
            catch (SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_receive is not null) await _receive;
            Upstream.Dispose();
            _stop.Dispose();
        }
    }
}
