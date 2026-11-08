using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Hosting;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Allocates actual UDP match hosts within Server. The lock protects allocation state only and never awaits
/// protocol initialization or cleanup.
/// </summary>
public sealed class NativeMatchAllocator : IMatchServerAllocator, IMatchServerHealthMonitor, IAsyncDisposable
{
    private readonly IPAddress listenAddress;
    private readonly string publicAddress;
    private readonly int publicPortBase;
    private readonly int maximumInstances;
    private readonly ServerLog log;
    private readonly INativeMatchProtocolFactory factory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ulong, Allocation> allocations = [];
    private bool disposed;

    /// <summary>
    /// The protocol factory is required. Callers must explicitly reject startup when the match protocol is
    /// unimplemented.
    /// </summary>
    public NativeMatchAllocator(ServerOptions server, ServerLog log, INativeMatchProtocolFactory factory,
        MatchServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(factory);
        options ??= server.Matches;
        if (!IPAddress.TryParse(server.ListenAddress, out var listen) || listen.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(server.PublicAddress, out var advertised) || advertised.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("原生对局监听地址和对外地址必须为 IPv4。");
        if (options.MaxInstances is < 1 or > 256 || options.PublicPortBase < 1024 ||
            (long)options.PublicPortBase + options.MaxInstances > 65536)
            throw new InvalidDataException("原生对局实例数或 UDP 端口范围无效。");
        listenAddress = listen;
        publicAddress = advertised.ToString();
        publicPortBase = options.PublicPortBase;
        maximumInstances = options.MaxInstances;
        this.log = log;
        this.factory = factory;
    }

    public async ValueTask<MatchEndpoint> ReserveAsync(GameRoom room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);

        List<Allocation> closing = [];
        Allocation allocation;
        Socket? socket = null;
        NativeMatchDescriptor? descriptor = null;

        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var entry in allocations.Values.Where(entry => entry.Host?.HasFailed == true))
                if (MarkClosing(entry)) closing.Add(entry);
            if (allocations.TryGetValue(room.RoomId, out var existing))
            {
                if (existing.Closing) throw new InvalidOperationException($"房间 {room.RoomId} 正在释放上一个回合，请在释放完成后重试。");
                allocation = existing;
            }
            else
            {
                descriptor = NativeMatchDescriptor.Capture(room);

                byte[] sessionKey = RandomNumberGenerator.GetBytes(16);

                try
                {
                    (int slot, socket) = BindFreeSlot();
                    allocation = new Allocation(room.RoomId, slot, new MatchEndpoint(publicAddress,
                        checked((ushort)((IPEndPoint)socket.LocalEndPoint!).Port), sessionKey));
                    allocations.Add(room.RoomId, allocation);
                }
                catch
                {
                    socket?.Dispose();
                    CryptographicOperations.ZeroMemory(sessionKey);
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
            foreach (var entry in closing) StartClosing(entry);
        }

        if (socket is not null)
        {
            // Create is synchronous protocol code and may be slow; a separate task avoids blocking other rooms and global health queries.
            _ = Task.Run(() => InitializeAsync(allocation, socket, descriptor!, cancellationToken));
        }
        await allocation.Ready.Task.WaitAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (allocation.Closing || !allocations.TryGetValue(room.RoomId, out var current) ||
                !ReferenceEquals(current, allocation))
                throw new OperationCanceledException($"房间 {room.RoomId} 原生对局在端点发布前已被释放。");
            if (allocation.Host is null || allocation.Host.HasFailed)
                throw new IOException($"房间 {room.RoomId} 原生对局在端点发布前已失败。", allocation.Host?.Failure);
            // Shutdown marks Closing under the same lock first, so copying after a successful check cannot race with key clearing.
            return CopyEndpoint(allocation.Endpoint);
        }
        finally { gate.Release(); }
    }

    private (int Slot, Socket Socket) BindFreeSlot()
    {
        for (int slot = 0; slot < maximumInstances; slot++)
        {
            // Allocations still initializing or closing retain their slots; reuse requires complete disposal.
            if (allocations.Values.Any(allocation => allocation.Slot == slot)) continue;
            try { return (slot, NativeMatchHost.BindSocket(new IPEndPoint(listenAddress, publicPortBase + slot))); }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
        }
        throw new InvalidOperationException("原生对局实例已满或所需 UDP 端口已被占用。");
    }

    private async Task InitializeAsync(Allocation allocation, Socket socket, NativeMatchDescriptor descriptor,
        CancellationToken callerToken)
    {
        NativeMatchHost? host = null;
        Exception? error = null;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(callerToken, allocation.Stopping.Token);

        try
        {
            host = await NativeMatchHost.StartAsync(socket, descriptor, factory, allocation.Endpoint.Key, startup.Token,
                message => log.Write("MATCH", message));
            startup.Token.ThrowIfCancellationRequested();
            if (host.HasFailed) throw new IOException($"房间 {allocation.RoomId} 原生对局宿主初始化失败。", host.Failure);
            log.Write("MATCH", $"房间 {allocation.RoomId} 原生对局宿主已绑定 {listenAddress}:{allocation.Endpoint.Port}，模式 0x{descriptor.ModeId:X8}，地图 0x{descriptor.MapId:X4}。");
        }
        catch (Exception exception) { error = exception; }

        bool startClosing = false;

        await gate.WaitAsync();
        try
        {
            allocation.Host = host;
            if (error is null && (disposed || allocation.Closing))
                error = new OperationCanceledException("原生对局在初始化期间被释放。");
            if (error is null)
            {
                // A11A sends these 16 bytes unchanged. The native protocol factory receives the same round key without inheriting the AFDEV bridge's fixed-zero-key limit.
                allocation.Ready.TrySetResult(allocation.Endpoint);
            }
            else
            {
                allocation.Ready.TrySetException(error);
                startClosing = MarkClosing(allocation);
            }
            allocation.Initialized.TrySetResult();
        }
        finally { gate.Release(); }
        if (startClosing) StartClosing(allocation);
    }

    public async ValueTask<bool> HasFailedAsync(ulong roomId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return !allocations.TryGetValue(roomId, out var allocation) || allocation.Closing || allocation.Host?.HasFailed == true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask ReleaseAsync(ulong roomId, CancellationToken cancellationToken)
    {
        Allocation? allocation;
        bool startClosing;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!allocations.TryGetValue(roomId, out allocation)) return;
            startClosing = MarkClosing(allocation);
        }
        finally { gate.Release(); }
        if (startClosing) StartClosing(allocation);
        // Ignore subsequent caller cancellation until removal finishes so room cleanup is never abandoned. Hold no global lock.
        await allocation.Closed.Task;
    }

    private static bool MarkClosing(Allocation allocation)
    {
        if (allocation.Closing) return false;
        allocation.Closing = true;
        return true;
    }

    private void StartClosing(Allocation allocation) => _ = Task.Run(() => CloseAsync(allocation));

    private async Task CloseAsync(Allocation allocation)
    {
        Exception? error = null;

        try { allocation.Stopping.Cancel(); }
        catch (Exception exception) { error = exception; }
        try
        {
            await allocation.Initialized.Task;
            if (allocation.Host is not null) await allocation.Host.DisposeAsync();
        }
        catch (Exception exception) { error ??= exception; }
        CryptographicOperations.ZeroMemory(allocation.Endpoint.Key);
        try
        {
            log.Write("MATCH", error is null ? $"房间 {allocation.RoomId} 原生对局宿主已释放。"
                : $"房间 {allocation.RoomId} 原生对局清理失败：{error.GetType().Name}。");
        }
        catch (Exception exception) { error ??= exception; }
        await gate.WaitAsync();
        try { allocations.Remove(allocation.RoomId); }
        finally { gate.Release(); }
        allocation.Stopping.Dispose();
        if (error is null) allocation.Closed.TrySetResult();
        else allocation.Closed.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        Allocation[] pending;
        List<Allocation> closing = [];

        await gate.WaitAsync();
        try
        {
            disposed = true;
            pending = allocations.Values.ToArray();
            foreach (var allocation in pending) if (MarkClosing(allocation)) closing.Add(allocation);
        }
        finally { gate.Release(); }
        foreach (var allocation in closing) StartClosing(allocation);
        await Task.WhenAll(pending.Select(allocation => allocation.Closed.Task));
    }

    private static MatchEndpoint CopyEndpoint(MatchEndpoint endpoint) => endpoint with { Key = (byte[])endpoint.Key.Clone() };

    private sealed class Allocation(ulong roomId, int slot, MatchEndpoint endpoint)
    {
        internal ulong RoomId { get; } = roomId;
        internal int Slot { get; } = slot;
        internal MatchEndpoint Endpoint { get; } = endpoint;
        internal NativeMatchHost? Host;
        internal bool Closing;
        internal CancellationTokenSource Stopping { get; } = new();
        internal TaskCompletionSource<MatchEndpoint> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
