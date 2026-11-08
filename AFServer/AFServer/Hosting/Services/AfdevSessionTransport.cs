using System.Net;
using System.Security.Cryptography;
using AFConnect.Proxy;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Match adapter for one authenticated ZONE connection. Retains only the session, TLS stream, and current
/// round supplied by the composition root. Auxiliary frames never consult a global online table or treat
/// UIN/StreamId as authorization. Holds no lock while awaiting listener ACKs.
/// </summary>
public sealed class AfdevSessionTransport(TunnelFramedStream stream, AfdevMatchAllocator allocator,
    Action<string>? log = null) : IMatchSessionTransport, IMatchPreparationTransport, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GameSession? session;
    private Admission? current;
    private readonly HashSet<(Guid Allocation, Guid Admission)> retiredAdmissions = [];
    private int disposed;
    private Guid preparationId;
    private Action? cancelPreparation;

    public async ValueTask ReportPreparationAsync(Guid operationId, AfdevPreparationStage stage, ushort attempt,
        ushort maximumAttempts, ushort remainingSeconds, Action cancel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await gate.WaitAsync(timeout.Token);
        try
        {
            RequireSession();
            if (stage == AfdevPreparationStage.Queued && preparationId == Guid.Empty)
                preparationId = operationId;
            if (preparationId != operationId)
                throw new InvalidDataException("准备通知不属于本会话当前操作。");

            cancelPreparation = cancel;
            if (stage is AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Completed)
            {
                // Even a failed terminal-state write must remove the old cancellation entry point. Logs record delivery status; failure cannot revive identity.
                cancelPreparation = null;
                preparationId = Guid.Empty;
            }
            await stream.WriteAuxiliaryAsync(TunnelFrameKind.Control,
                AfdevTunnelProtocol.EncodePreparation(operationId, stage, attempt, maximumAttempts, remainingSeconds), timeout.Token);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Call once only after game SYNACK authentication completes. Reconnecting with the same UIN still
    /// requires a new adapter.
    /// </summary>
    public void Bind(GameSession authenticatedSession)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (Interlocked.CompareExchange(ref session, authenticatedSession, null) is not null)
            throw new InvalidOperationException("ZONE 对局适配器已经绑定精确会话。");
    }

    public async ValueTask<MatchEndpoint> PrepareAsync(MatchStartSnapshot snapshot, MatchAdmissionRound round,
        MatchAdmissionTicket ticket, MatchEndpoint endpoint, CancellationToken cancellationToken)
    {
        var owner = RequireSession();
        var member = snapshot.Members.SingleOrDefault(value => ReferenceEquals(value.Session, owner));

        if (member is null || snapshot.AllocationId != round.AllocationId || ticket.AllocationId != round.AllocationId ||
            !ReferenceEquals(allocator.GetSnapshot(snapshot.AllocationId), snapshot))
            throw new InvalidOperationException("开赛快照、资格与此认证连接不匹配。");

        var admission = new Admission(round, ticket.AdmissionId, member.Profile.Nickname);

        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireSession();
            if (current is not null) throw new InvalidOperationException("本 ZONE 会话仍持有另一回合。");
            if (retiredAdmissions.Count >= 4096)
                throw new InvalidDataException("本连接对局数量超出上限，请重新连接。");
            Volatile.Write(ref current, admission);
        }
        finally { gate.Release(); }

        byte[] secret = ticket.CopySecret();
        byte[] control = AfdevTunnelProtocol.EncodeControl(AfdevControlKind.Prepare, round.AllocationId,
            ticket.AdmissionId, secret);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // The caller supplies the remaining budget for this click. This limit protects nonlobby callers and cannot extend that budget.
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await gate.WaitAsync(timeout.Token);
            try
            {
                // Keep preparation frames ordered with revocation registration. Hold the lock only for frame writes, not while awaiting Connect ACKs.
                RequireAdmission(round.AllocationId, ticket.AdmissionId);
                await stream.WriteAuxiliaryAsync(TunnelFrameKind.Control, control, timeout.Token);
            }
            finally { gate.Release(); }

            ushort port = await admission.Prepared.Task.WaitAsync(timeout.Token);

            timeout.Token.ThrowIfCancellationRequested();
            await gate.WaitAsync(timeout.Token);
            try { _ = RequireAdmission(round.AllocationId, ticket.AdmissionId); }
            finally { gate.Release(); }

            log?.Invoke($"LOCAL_READY session={owner.SessionId:N} allocation={round.AllocationId:N} admission={ticket.AdmissionId:N} port={port}");
            return endpoint with { Address = IPAddress.Loopback.ToString(), Port = port };
        }
        catch
        {
            await RevokeAsync(round.AllocationId, CancellationToken.None);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(control);
        }
    }

    /// <summary>
    /// Dispatched serially by the sole TLS reader. Never waits here for later inbound frames or the lobby
    /// lock.
    /// </summary>
    public async ValueTask HandleFrameAsync(TunnelFrameKind kind, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var owner = RequireSession();

        if (kind == TunnelFrameKind.Control)
        {
            var message = AfdevTunnelProtocol.DecodeControl(payload);

            if (message.Kind == AfdevControlKind.CancelPreparation)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    // Late cancellation cannot affect a new operation or published world, nor find a replacement target by room ID.
                    if (preparationId == message.AllocationId && cancelPreparation is { } cancel) cancel();
                }
                finally { gate.Release(); }
                return;
            }

            if (message.Kind == AfdevControlKind.Revoke)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (retiredAdmissions.Contains((message.AllocationId, message.AdmissionId))) return;

                    var active = current;

                    if (active is null || active.Round.AllocationId != message.AllocationId || active.AdmissionId != message.AdmissionId)
                        throw new InvalidDataException("客户端撤销不属于此会话当前资格。");

                    active.Releasing = true;
                    active.ClientRevoke ??= Task.Run(async () =>
                    {
                        try { await RevokeAsync(active.Round.AllocationId, CancellationToken.None); }
                        catch (Exception error)
                        {
                            log?.Invoke($"CLIENT_REVOKE_FAILED allocation={active.Round.AllocationId:N} kind={error.GetType().Name}");
                            stream.Abort();
                        }
                    });
                }
                finally { gate.Release(); }

                // The sole TLS read loop must resume dispatching A117 immediately. The same admission's cleanup task owns slow native cleanup.
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var active = RequireAdmission(message.AllocationId, message.AdmissionId);

                switch (message.Kind)
                {
                    case AfdevControlKind.Prepared:
                        if (!active.Prepared.TrySetResult(checked((ushort)message.LocalPort)))
                            throw new InvalidDataException("本地监听 ACK 重放。");
                        break;
                    case AfdevControlKind.Redeem:
                        if (!active.Prepared.Task.IsCompletedSuccessfully || active.Lease is not null ||
                            !active.Round.TryRedeem(owner, message.AdmissionId, message.Secret.Span, message.Endpoint!, out var lease))
                            throw new InvalidDataException("资格兑换被拒绝：过期、重放、错流或回合已撤销。");

                        active.Lease = lease!;
                        active.Relay = new AfdevLeaseRelay(allocator.GetReadyHost(message.AllocationId), active.Round,
                            lease!, active.Nickname, (bytes, token) => SendDatagramAsync(active, bytes, token), log);
                        active.Watch = ObserveRelayAsync(active, active.Relay);
                        await SendControlAsync(AfdevControlKind.Accepted, active, cancellationToken);
                        log?.Invoke($"LEASE_ACCEPTED session={owner.SessionId:N} allocation={message.AllocationId:N} admission={message.AdmissionId:N} UIN={owner.Uin}");
                        break;
                    default:
                        throw new InvalidDataException("连接端发送了服务端专属的对局控制消息。");
                }
            }
            finally { gate.Release(); }
            return;
        }
        if (kind != TunnelFrameKind.Datagram)
            throw new InvalidDataException("未知对局辅助帧。");

        var datagram = AfdevTunnelProtocol.DecodeDatagram(payload);

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (retiredAdmissions.Contains((datagram.AllocationId, datagram.AdmissionId))) return;

            var active = current;

            if (active is null || active.Round.AllocationId != datagram.AllocationId || active.AdmissionId != datagram.AdmissionId)
                throw new InvalidDataException("对局帧不属于此认证流的当前 allocation/admission。");

            // A117 races with the independent reader. Once eligibility is revoked, discard old CONNECT packets already written without breaking the lobby stream.
            if (active.Releasing || active.Lease is { IsRevoked: true }) return;
            if (active.Lease is null || active.Relay is null)
                throw new InvalidDataException("数据报没有本会话已兑换的当前资格。");
            active.Relay.ForwardClientDatagram(datagram.Payload.Span);
        }
        finally { gate.Release(); }
    }

    private async ValueTask SendDatagramAsync(Admission admission, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lease = admission.Lease;

            if (!ReferenceEquals(current, admission) || admission.Releasing || lease is null ||
                !admission.Round.IsCurrent(lease, lease.RemoteEndpoint)) return;

            byte[] frame = AfdevTunnelProtocol.EncodeDatagram(admission.Round.AllocationId, admission.AdmissionId, payload.Span);
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Finish every started frame. Normal departure must not truncate it with the relay cancellation token and corrupt the entire ZONE stream.
            // Revoke executes behind the same adapter lock. Late writes retain the old identity and finish before the revocation control message.
            await stream.WriteAuxiliaryAsync(TunnelFrameKind.Datagram, frame, writeTimeout.Token);
        }
        finally { gate.Release(); }
    }

    private async Task ObserveRelayAsync(Admission admission, AfdevLeaseRelay relay)
    {
        await relay.Completion;
        if (relay.Failure is not null && !admission.Releasing && ReferenceEquals(Volatile.Read(ref current), admission))
        {
            // A failed native read or reply may receive no further client packets. End the owning stream immediately to trigger exact-session disconnect cleanup.
            // Revoking only the lease and waiting for another upstream packet would leave an unusable world occupying a slot.
            stream.Abort();
        }
    }

    public async ValueTask RevokeAsync(Guid allocationId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (current is null || current.Round.AllocationId != allocationId) return;

            // Register departure intent before the seconds-long bot-stop wait so in-flight read failures do not incorrectly disconnect ZONE during that gap.
            // Actual eligibility revocation and upstream disposal still follow native-task shutdown. Repeated calls join the same cleanup.
            current.Releasing = true;
        }
        finally { gate.Release(); }

        // Stop new bot work and finish native commands before revoking eligibility. Wait without the stream lock to avoid deadlocking in-flight replies.
        if (session is { } owner)
        {
            await allocator.PreserveLeavingSettlementAsync(allocationId, owner.SessionId).ConfigureAwait(false);
            await allocator.StopBotWorkAsync(allocationId, owner.SessionId).ConfigureAwait(false);
        }

        Admission revoked;
        Task release;
        TaskCompletionSource? completion = null;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (current is null || current.Round.AllocationId != allocationId) return;

            revoked = current;
            if (revoked.Release is null || revoked.Release.IsFaulted || revoked.Release.IsCanceled)
            {
                // Revoke eligibility and register the sole cleanup task first, then stop the relay outside the lock. Concurrent departures and disconnects join the same cleanup.
                // On failure, retain current and its revocation flag. Later calls retry cleanup only, never redeem again or prepare another round.
                revoked.Releasing = true;
                if (session is not null) revoked.Round.Revoke(session);
                revoked.Prepared.TrySetCanceled();

                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                revoked.Release = completion.Task;
            }

            release = revoked.Release;
        }
        finally { gate.Release(); }

        // Start after releasing gate so an in-flight relay reply cannot create a circular wait on the same lock.
        if (completion is not null) _ = ReleaseAdmissionAsync(revoked, completion);

        // Callers may cancel only their own waits. Shared cleanup and its control-frame writes have an independent lifetime.
        await release.WaitAsync(cancellationToken);
    }

    private async Task ReleaseAdmissionAsync(Admission revoked, TaskCompletionSource completion)
    {
        Exception? failure = null;

        try
        {
            if (revoked.Relay is not null) await revoked.Relay.DisposeAsync();
            await revoked.Watch;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await SendControlAsync(AfdevControlKind.Revoke, revoked, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Reclaim the native upstream even after this TLS stream ends. Eligibility was revoked before any asynchronous shutdown.
                stream.Abort();
                log?.Invoke($"REVOKE_CHANNEL_CLOSED allocation={revoked.Round.AllocationId:N} kind={exception.GetType().Name}");
            }

            log?.Invoke($"REVOKED session={session?.SessionId:N} allocation={revoked.Round.AllocationId:N} admission={revoked.AdmissionId:N}");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if (failure is not null)
            {
                completion.TrySetException(failure);
                return;
            }

            // Release the session reservation only after the relay, observation task, and revocation control finish. Returning waiters may then continue host cleanup.
            retiredAdmissions.Add((revoked.Round.AllocationId, revoked.AdmissionId));
            if (ReferenceEquals(current, revoked)) Volatile.Write(ref current, null);
            completion.TrySetResult();
        }
        finally { gate.Release(); }
    }

    private ValueTask SendControlAsync(AfdevControlKind kind, Admission admission, CancellationToken cancellationToken) =>
        stream.WriteAuxiliaryAsync(TunnelFrameKind.Control,
            AfdevTunnelProtocol.EncodeControl(kind, admission.Round.AllocationId, admission.AdmissionId), cancellationToken);

    private GameSession RequireSession()
    {
        var owner = Volatile.Read(ref session);

        if (Volatile.Read(ref disposed) != 0 || owner is null || owner.IsDisconnected)
            throw new InvalidDataException("对局控制需要此 TLS 流已完成认证的活动 ZONE 会话。");
        return owner;
    }

    private Admission RequireAdmission(Guid allocationId, Guid admissionId)
    {
        var active = Volatile.Read(ref current);

        RequireSession();
        if (active is null || active.Releasing || active.Round.AllocationId != allocationId || active.AdmissionId != admissionId)
            throw new InvalidDataException("对局帧不属于此认证流的当前 allocation/admission。");
        return active;
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);

        Admission? active;

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            // Wait for preparation registration that passed RequireSession; do not mistake current for empty before that registration writes it.
            // Repeated Dispose calls also retrieve the current reservation and join shared cleanup; disposed must not cause an early return.
            active = current;
        }
        finally { gate.Release(); }

        if (active is not null) await RevokeAsync(active.Round.AllocationId, CancellationToken.None);
    }

    private sealed class Admission(MatchAdmissionRound round, Guid admissionId, string nickname)
    {
        internal MatchAdmissionRound Round { get; } = round;
        internal Guid AdmissionId { get; } = admissionId;
        internal string Nickname { get; } = nickname;
        internal TaskCompletionSource<ushort> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MatchAdmissionLease? Lease { get; set; }
        internal AfdevLeaseRelay? Relay { get; set; }
        internal Task Watch { get; set; } = Task.CompletedTask;
        internal bool Releasing { get; set; }
        internal Task? Release { get; set; }
        internal Task? ClientRevoke { get; set; }
    }
}
