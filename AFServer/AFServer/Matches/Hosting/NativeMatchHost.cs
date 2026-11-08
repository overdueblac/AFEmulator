using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AFServer.AssaultFire.Matches.Protocol;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// IPv4 UDP host for one room, retaining the actual bound socket until round disposal and starting no
/// external processes.
/// </summary>
public sealed class NativeMatchHost : IAsyncDisposable
{
    /// <summary>
    /// Maximum IPv4 UDP payload. Protocols cannot construct datagrams above the network limit.
    /// </summary>
    public const int MaximumUdpPayloadBytes = 65507;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RejectionLogInterval = TimeSpan.FromSeconds(5);
    private readonly Socket socket;
    private readonly INativeMatchProtocol protocol;
    private readonly CancellationTokenSource stopping = new();
    private readonly Action<string>? log;
    private readonly int maximumDatagramBytes;
    private readonly IPEndPoint localEndpoint;
    private readonly object disposalLock = new();
    private readonly Task completion;
    private Exception? failure;
    private Task? disposal;
    private long rejectedDatagrams;
    private long pendingRejectedDatagrams;
    private long pendingRejectedBytes;
    private int largestRejectedDatagram;
    private long lastRejectionReport = Stopwatch.GetTimestamp();

    private NativeMatchHost(Socket socket, NativeMatchDescriptor descriptor, INativeMatchProtocol protocol,
        Action<string>? log, int maximumDatagramBytes)
    {
        this.socket = socket;
        this.protocol = protocol;
        this.log = log;
        this.maximumDatagramBytes = maximumDatagramBytes;
        Descriptor = descriptor;
        localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
        completion = RunAsync();
    }

    public NativeMatchDescriptor Descriptor { get; }
    public IPEndPoint LocalEndpoint => new(localEndpoint.Address, localEndpoint.Port);
    /// <summary>
    /// Completes when host scheduling stops. Failure distinguishes runtime faults from normal release.
    /// </summary>
    public Task Completion => completion;
    public Exception? Failure => Volatile.Read(ref failure);
    public bool HasFailed => Failure is not null;
    /// <summary>
    /// Datagrams dropped since startup for exceeding the protocol length limit, excluding protocol-level
    /// rejections.
    /// </summary>
    public long RejectedDatagramCount => Interlocked.Read(ref rejectedDatagrams);

    /// <summary>
    /// Exclusively binds the port and initializes the supported protocol. Any initialization failure
    /// reclaims the socket.
    /// </summary>
    public static ValueTask<NativeMatchHost> BindAsync(IPEndPoint endpoint, NativeMatchDescriptor descriptor,
        INativeMatchProtocolFactory factory, ReadOnlyMemory<byte> sessionKey, CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        ValidateArguments(descriptor, factory, sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        return StartAsync(BindSocket(endpoint), descriptor, factory, sessionKey, cancellationToken, log);
    }

    internal static Socket BindSocket(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("原生对局宿主当前只支持 IPv4。", nameof(endpoint));

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(endpoint);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    internal static async ValueTask<NativeMatchHost> StartAsync(Socket boundSocket, NativeMatchDescriptor descriptor,
        INativeMatchProtocolFactory factory, ReadOnlyMemory<byte> sessionKey, CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        INativeMatchProtocol? protocol = null;

        try
        {
            int maximumDatagramBytes = ValidateArguments(descriptor, factory, sessionKey);

            cancellationToken.ThrowIfCancellationRequested();
            protocol = factory.Create(descriptor, sessionKey.Span)
                ?? throw new InvalidOperationException("原生对局协议工厂未提供可用协议，拒绝发布对局端点。");
            cancellationToken.ThrowIfCancellationRequested();
            return new NativeMatchHost(boundSocket, descriptor, protocol, log, maximumDatagramBytes);
        }
        catch
        {
            boundSocket.Dispose();
            if (protocol is not null) await protocol.DisposeAsync();
            throw;
        }
    }

    private static int ValidateArguments(NativeMatchDescriptor descriptor, INativeMatchProtocolFactory factory, ReadOnlyMemory<byte> sessionKey)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);
        if (sessionKey.Length != NativeDsDatagramCodec.KeyBytes)
            throw new ArgumentException("原生对局密钥必须为十六字节。", nameof(sessionKey));

        int maximumDatagramBytes = factory.MaximumDatagramBytes;

        if (maximumDatagramBytes is < 1 or > MaximumUdpPayloadBytes)
            throw new InvalidDataException("原生协议工厂的数据报长度上限必须为1至65507字节。");
        return maximumDatagramBytes;
    }

    private async Task RunAsync()
    {
        // Receive the full UDP datagram before checking length so Windows does not turn truncation into a MessageSize socket failure.
        byte[] buffer = new byte[ushort.MaxValue];
        using var timer = new PeriodicTimer(TickInterval);
        CancellationToken token = stopping.Token;
        Task<SocketReceiveFromResult>? receive = null;
        Task<bool>? tick = null;
        long lastTick = Stopwatch.GetTimestamp();

        try
        {
            receive = ReceiveAsync(buffer, token);
            tick = timer.WaitForNextTickAsync(token).AsTask();
            while (true)
            {
                await Task.WhenAny(receive, tick);
                token.ThrowIfCancellationRequested();
                // Continuous inbound traffic must not starve the simulation clock. All callbacks execute serially in this loop.
                if (tick.IsCompleted)
                {
                    if (!await tick) throw new IOException("原生对局时钟意外停止。");
                    tick = null;

                    long now = Stopwatch.GetTimestamp();

                    await protocol.TickAsync(Stopwatch.GetElapsedTime(lastTick, now), SendAsync, token);
                    ReportRejectedDatagrams(false);
                    lastTick = now;
                    tick = timer.WaitForNextTickAsync(token).AsTask();
                }
                if (receive.IsCompleted)
                {
                    SocketReceiveFromResult packet = await receive;

                    receive = null;
                    if (packet.ReceivedBytes <= maximumDatagramBytes)
                        await protocol.HandleDatagramAsync(buffer.AsMemory(0, packet.ReceivedBytes),
                            (IPEndPoint)packet.RemoteEndPoint, SendAsync, token);
                    else
                    {
                        Interlocked.Increment(ref rejectedDatagrams);
                        pendingRejectedDatagrams++;
                        pendingRejectedBytes += packet.ReceivedBytes;
                        largestRejectedDatagram = Math.Max(largestRejectedDatagram, packet.ReceivedBytes);
                    }
                    receive = ReceiveAsync(buffer, token);
                }
            }
        }
        catch (Exception error) when (stopping.IsCancellationRequested &&
            error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception error)
        {
            Volatile.Write(ref failure, error);
            log?.Invoke($"房间 {Descriptor.RoomId} 原生对局宿主失败：{error.GetType().Name}。");
        }
        finally
        {
            stopping.Cancel();
            socket.Dispose();
            await ObservePendingAsync(receive);
            await ObservePendingAsync(tick);
            ReportRejectedDatagrams(true);
        }
    }

    private void ReportRejectedDatagrams(bool final)
    {
        long now = Stopwatch.GetTimestamp();

        if (pendingRejectedDatagrams == 0 || (!final && Stopwatch.GetElapsedTime(lastRejectionReport, now) < RejectionLogInterval)) return;
        // Unauthenticated datagrams update memory counts only. Log at most one summary per room every five seconds, flushing remaining counts on release.
        log?.Invoke($"房间 {Descriptor.RoomId} 超长 UDP 汇总：拒绝 {pendingRejectedDatagrams} 个，共 {pendingRejectedBytes} 字节，最大 {largestRejectedDatagram} 字节，上限 {maximumDatagramBytes} 字节。");
        pendingRejectedDatagrams = 0;
        pendingRejectedBytes = 0;
        largestRejectedDatagram = 0;
        lastRejectionReport = now;
    }

    private async Task<SocketReceiveFromResult> ReceiveAsync(byte[] buffer, CancellationToken token)
    {
        while (true)
        {
            try
            {
                return await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
            }
            catch (SocketException error) when (!token.IsCancellationRequested &&
                error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                // ICMP errors from a closed UDP peer do not mean the whole room listener failed.
            }
        }
    }

    private async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint peer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (datagram.Length > maximumDatagramBytes)
            throw new InvalidDataException($"原生对局出站数据报超过 {maximumDatagramBytes} 字节。");
        if (peer.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("原生对局出站地址必须为 IPv4。");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        int sent = await socket.SendToAsync(datagram, SocketFlags.None, peer, cancellation.Token);

        if (sent != datagram.Length) throw new IOException("原生对局 UDP 数据报未完整发送。");
    }

    private static async Task ObservePendingAsync(Task? task)
    {
        if (task is null) return;
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    /// <summary>
    /// Cancels network I/O and waits for the current callback before disposing the protocol.
    /// Concurrent/repeated disposal shares one task.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalLock) return new ValueTask(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        stopping.Cancel();
        socket.Dispose();
        try { await completion; }
        finally
        {
            try { await protocol.DisposeAsync(); }
            finally { stopping.Dispose(); }
        }
    }
}
