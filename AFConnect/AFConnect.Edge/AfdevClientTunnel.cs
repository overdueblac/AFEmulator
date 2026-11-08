using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using AFConnect.Proxy;

namespace AFConnect.EDGE;

/// <summary>
/// Owns the local AFDEV adapter for one authenticated ZONE stream. Listener preparation, ticket redemption,
/// endpoint binding, and revocation share this lifetime. Bind the first endpoint only after verifying
/// process ownership. While awaiting Accepted, buffer at most 8 packets, 128 KiB, or 5 seconds; never
/// migrate automatically.
/// </summary>
internal sealed class AfdevClientTunnel(Func<IPEndPoint, bool> ownsEndpoint, Action<string>? log = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private TunnelFramedStream? stream;
    private Route? route;
    private int disposed;
    private readonly HashSet<Guid> finishedPreparations = [];
    private readonly HashSet<(Guid Allocation, Guid Admission)> retiredRoutes = [];
    private Guid preparation;
    private bool cancelRequested;
    private AfdevPreparationStage preparationStage;

    internal async ValueTask<bool> CancelPreparationAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (disposed != 0 || preparation == Guid.Empty || cancelRequested) return false;

            byte[] bytes = AfdevTunnelProtocol.EncodeControl(AfdevControlKind.CancelPreparation, preparation, preparation);

            await stream!.WriteAuxiliaryAsync(TunnelFrameKind.Control, bytes, token);
            cancelRequested = true;
            log?.Invoke("[对局] 已请求取消，正在等待服务器确认并回收；确认前请勿重复开赛。");
            return true;
        }
        finally { gate.Release(); }
    }

    private void HandlePreparation(AfdevControlMessage message)
    {
        if (finishedPreparations.Contains(message.AllocationId)) return;
        if (preparation == Guid.Empty)
        {
            if (message.Stage != AfdevPreparationStage.Queued)
                throw new InvalidDataException("准备进度缺少初始登记。");
            preparation = message.AllocationId;
            cancelRequested = false;
        }
        if (preparation != message.AllocationId)
            throw new InvalidDataException("另一准备操作尚未结束。");
        if (cancelRequested && message.Stage is not (AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Cancelling or AfdevPreparationStage.Completed))
            return;
        if (preparationStage == AfdevPreparationStage.Cancelling && message.Stage is not (AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Cancelling))
            return;

        preparationStage = message.Stage;
        string text = message.Stage switch
        {
            AfdevPreparationStage.Queued => "已登记开赛，正在准备。输入 cancel 并回车可取消，也可在游戏内离房或更改设置。",
            AfdevPreparationStage.Copying => "正在准备地图资源。",
            AfdevPreparationStage.Starting => "正在加载地图。",
            AfdevPreparationStage.Retrying => "本次加载未完成，清理后正在重新尝试。",
            AfdevPreparationStage.Connecting => "地图已加载，正在建立游戏连接。",
            AfdevPreparationStage.Cancelled => "准备已取消，可以重新开赛。",
            AfdevPreparationStage.Failed => "准备失败；若资源仍在清理，请等待清理完成后重新开赛。",
            AfdevPreparationStage.Completed => "准备完成，正在进入地图。",
            AfdevPreparationStage.Cancelling => "正在取消并清理，请稍候。",
            _ => throw new InvalidDataException("未知准备阶段。")
        };

        log?.Invoke($"[对局] {text}（尝试 {message.Attempt}/{message.MaximumAttempts}，总等待剩余最多 {message.RemainingSeconds} 秒）");
        if (message.Stage is AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Completed)
        {
            // Keep the tombstone for the exact ZONE lifetime so late state cannot recreate a wait or overwrite the next round.
            if (finishedPreparations.Count >= 4096) throw new InvalidDataException("本连接准备操作数量超出上限，请重新连接。");
            finishedPreparations.Add(preparation);
            preparation = Guid.Empty;
            preparationStage = default;
        }
    }

    internal void Bind(TunnelFramedStream framed) => stream = framed;

    internal async ValueTask HandleAsync(TunnelFrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        Route? retired = null;

        await gate.WaitAsync(token);
        try
        {
            if (disposed != 0) throw new ObjectDisposedException(nameof(AfdevClientTunnel));
            if (kind == TunnelFrameKind.Datagram)
            {
                var message = AfdevTunnelProtocol.DecodeDatagram(payload);

                if (retiredRoutes.Contains((message.AllocationId, message.AdmissionId))) return;

                var current = RequireRoute(message.AllocationId, message.AdmissionId);

                if (current.RevokeRequested) return;
                if (!current.Accepted || current.Endpoint is null)
                    throw new InvalidDataException("AFDEV 下行早于资格兑换。");
                if (!ownsEndpoint(current.Endpoint))
                {
                    await RequestRouteRevokeAsync(current, "client-endpoint-closed", token);
                    return;
                }
                await current.Socket.SendToAsync(message.Payload, SocketFlags.None, current.Endpoint, token);
                return;
            }

            var control = AfdevTunnelProtocol.DecodeControl(payload);

            if (control.Kind == AfdevControlKind.Preparation)
            {
                HandlePreparation(control);
                return;
            }
            if (control.Kind == AfdevControlKind.CancelPreparation)
                throw new InvalidDataException("服务器不能请求连接端取消准备。");

            if (control.Kind == AfdevControlKind.Prepare)
            {
                if (finishedPreparations.Contains(control.AllocationId) ||
                    (preparation != Guid.Empty && preparation != control.AllocationId))
                    throw new InvalidDataException("资格准备不属于当前有效准备操作。");
                if (route is not null) throw new InvalidDataException("当前 ZONE 已有 AFDEV 准备或活动资格。");
                if (retiredRoutes.Count >= 4096)
                    throw new InvalidDataException("本连接对局数量超出上限，请重新连接。");

                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ExclusiveAddressUse = true
                };

                try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); }
                catch { socket.Dispose(); throw; }

                var prepared = new Route(control.AllocationId, control.AdmissionId, control.Secret.ToArray(), socket,
                    CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));

                route = prepared;
                prepared.Loop = ReceiveAsync(prepared);
                await SendControlAsync(AfdevControlKind.Prepared, prepared, token,
                    localPort: ((IPEndPoint)socket.LocalEndPoint!).Port);
                log?.Invoke($"AFDEV LOCAL_READY allocation={prepared.AllocationId:N} admission={prepared.AdmissionId:N} port={((IPEndPoint)socket.LocalEndPoint!).Port}");
                return;
            }

            if (control.Kind == AfdevControlKind.Revoke &&
                retiredRoutes.Contains((control.AllocationId, control.AdmissionId))) return;

            var active = RequireRoute(control.AllocationId, control.AdmissionId);

            if (control.Kind == AfdevControlKind.Revoke)
            {
                route = null;
                retiredRoutes.Add((active.AllocationId, active.AdmissionId));
                active.Stop();
                retired = active;
                log?.Invoke($"AFDEV REVOKED allocation={active.AllocationId:N} admission={active.AdmissionId:N}");
                return;
            }
            if (control.Kind != AfdevControlKind.Accepted || active.Accepted || active.Endpoint is null ||
                active.FirstPacketAt is null || Stopwatch.GetElapsedTime(active.FirstPacketAt.Value) >= TimeSpan.FromSeconds(5))
                throw new InvalidDataException("AFDEV 控制消息时序无效。");

            active.Accepted = true;
            CryptographicOperations.ZeroMemory(active.Secret);
            log?.Invoke($"AFDEV LEASE_ACCEPTED allocation={active.AllocationId:N} admission={active.AdmissionId:N}");
            while (active.Pending.TryDequeue(out var packet))
            {
                try { await SendDatagramAsync(active, packet, token); }
                finally { CryptographicOperations.ZeroMemory(packet); }
            }
            active.PendingBytes = 0;
        }
        finally
        {
            gate.Release();
            if (retired is not null) await retired.JoinAsync();
        }
    }

    private async Task ReceiveAsync(Route current)
    {
        byte[] buffer = new byte[65507];

        try
        {
            while (!current.Lifetime.IsCancellationRequested)
            {
                var packet = await current.Socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), current.Lifetime.Token);
                var endpoint = (IPEndPoint)packet.RemoteEndPoint;

                await gate.WaitAsync(current.Lifetime.Token);
                try
                {
                    if (!ReferenceEquals(route, current)) return;
                    if (packet.ReceivedBytes is < 1 or > AfdevTunnelProtocol.MaximumPayloadLength) continue;
                    if (current.Endpoint is null)
                    {
                        if (!ownsEndpoint(endpoint)) continue;
                        current.Endpoint = new(endpoint.Address, endpoint.Port);
                        current.FirstPacketAt = Stopwatch.GetTimestamp();
                        await SendControlAsync(AfdevControlKind.Redeem, current, current.Lifetime.Token);
                        current.Expiry = ExpirePendingAsync(current);
                        log?.Invoke($"AFDEV ENDPOINT_PROVED allocation={current.AllocationId:N} admission={current.AdmissionId:N} port={endpoint.Port}");
                    }
                    else if (!current.Endpoint.Equals(endpoint))
                    {
                        // Late packets and other ports cannot replace an endpoint awaiting or completing redemption.
                        continue;
                    }
                    else if (!ownsEndpoint(endpoint))
                    {
                        await RequestRouteRevokeAsync(current, "client-endpoint-closed", lifetime.Token);
                        return;
                    }
                    if (current.Accepted)
                    {
                        await SendDatagramAsync(current, buffer.AsMemory(0, packet.ReceivedBytes), current.Lifetime.Token);
                    }
                    else
                    {
                        if (current.Pending.Count >= 8 || current.PendingBytes + packet.ReceivedBytes > 128 * 1024)
                            throw new IOException("AFDEV 首包等待队列已满。");
                        current.Pending.Enqueue(buffer.AsSpan(0, packet.ReceivedBytes).ToArray());
                        current.PendingBytes += packet.ReceivedBytes;
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (Exception exception) when (current.Lifetime.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException or SocketException) { }
        catch (Exception exception)
        {
            // Closing redeemed local UDP revokes only this match. Returning to the lobby also closes the endpoint, so the settlement stream must remain open.
            if (exception is SocketException && current.Accepted)
            {
                await gate.WaitAsync();
                try
                {
                    if (ReferenceEquals(route, current))
                        await RequestRouteRevokeAsync(current, "local-socket-closed", lifetime.Token);
                    return;
                }
                catch (Exception revokeError)
                {
                    log?.Invoke($"AFDEV REVOKE_SEND_FAILED allocation={current.AllocationId:N} kind={revokeError.GetType().Name}");
                }
                finally { gate.Release(); }
            }

            // Frame-write and identity-validation failures still terminate the exact TLS connection; game data cannot continue over a potentially truncated transport.
            stream?.Dispose();
            await gate.WaitAsync();
            try { current.Stop(); }
            finally { gate.Release(); }
            log?.Invoke($"AFDEV UDP_FAILED allocation={current.AllocationId:N} admission={current.AdmissionId:N} kind={exception.GetType().Name}");
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    /// <summary>
    /// The caller holds gate. Retain the original identity until the server confirms revocation; reject new
    /// grants and discard late downstream packets for this round meanwhile.
    /// </summary>
    private async ValueTask RequestRouteRevokeAsync(Route current, string reason, CancellationToken token)
    {
        if (current.RevokeRequested) return;

        current.RevokeRequested = true;
        current.Stop();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await SendControlAsync(AfdevControlKind.Revoke, current, timeout.Token);
        log?.Invoke($"AFDEV LOCAL_REVOKE allocation={current.AllocationId:N} admission={current.AdmissionId:N} reason={reason}");
    }

    private async Task ExpirePendingAsync(Route current)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), current.Lifetime.Token);
            await gate.WaitAsync(current.Lifetime.Token);
            try
            {
                if (ReferenceEquals(route, current) && !current.Accepted)
                {
                    stream?.Dispose();
                    current.Stop();
                    log?.Invoke($"AFDEV FIRST_PACKET_EXPIRED allocation={current.AllocationId:N} admission={current.AdmissionId:N}");
                }
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (current.Lifetime.IsCancellationRequested) { }
    }

    private async ValueTask SendControlAsync(AfdevControlKind kind, Route current, CancellationToken token, int localPort = 0)
    {
        byte[] payload = AfdevTunnelProtocol.EncodeControl(kind, current.AllocationId, current.AdmissionId,
            current.Secret, localPort, current.Endpoint);

        try { await stream!.WriteAuxiliaryAsync(TunnelFrameKind.Control, payload, token); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async ValueTask SendDatagramAsync(Route current, ReadOnlyMemory<byte> packet, CancellationToken token)
    {
        byte[] payload = AfdevTunnelProtocol.EncodeDatagram(current.AllocationId, current.AdmissionId, packet.Span);

        try { await stream!.WriteAuxiliaryAsync(TunnelFrameKind.Datagram, payload, token); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private Route RequireRoute(Guid allocationId, Guid admissionId) =>
        route is { } current && current.AllocationId == allocationId && current.AdmissionId == admissionId
            ? current : throw new InvalidDataException("AFDEV 消息不属于本流的当前资格。");

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync();

        Route? stopped;

        try
        {
            if (disposed != 0) return;
            disposed = 1;
            stopped = route;
            route = null;
            stopped?.Stop();
        }
        finally { gate.Release(); }
        if (stopped is not null)
        {
            await stopped.JoinAsync();
        }
    }

    private sealed class Route(Guid allocationId, Guid admissionId, byte[] secret, Socket socket,
        CancellationTokenSource cancellation)
    {
        internal Guid AllocationId { get; } = allocationId;
        internal Guid AdmissionId { get; } = admissionId;
        internal byte[] Secret { get; } = secret;
        internal Socket Socket { get; } = socket;
        internal CancellationTokenSource Lifetime { get; } = cancellation;
        internal Queue<byte[]> Pending { get; } = new();
        internal int PendingBytes { get; set; }
        internal IPEndPoint? Endpoint { get; set; }
        internal long? FirstPacketAt { get; set; }
        internal bool Accepted { get; set; }
        internal bool RevokeRequested { get; set; }
        internal Task Loop { get; set; } = Task.CompletedTask;
        internal Task Expiry { get; set; } = Task.CompletedTask;

        internal void Stop()
        {
            Lifetime.Cancel();
            Socket.Dispose();
            CryptographicOperations.ZeroMemory(Secret);
            while (Pending.TryDequeue(out var packet)) CryptographicOperations.ZeroMemory(packet);
            PendingBytes = 0;
        }

        internal async ValueTask JoinAsync()
        {
            await Loop;
            await Expiry;
            Lifetime.Dispose();
        }
    }
}
