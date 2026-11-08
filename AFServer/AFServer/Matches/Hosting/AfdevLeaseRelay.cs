using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches.Protocol;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Each redeemed lease owns one loopback upstream socket. Its downstream is only the exact CONNECT channel
/// that created it; no public UDP entry point is provided.
/// </summary>
/// <remarks>
/// Synchronous upstream submission and revocation share the round lock. Stop revokes first, cancels
/// receiving, waits for task completion, then releases the socket. Downstream writes must carry the
/// original allocation/admission IDs and recheck this lease at actual write time. Callbacks must not cache
/// authorization.
/// </remarks>
public sealed partial class AfdevLeaseRelay : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly AfdevProcessHost host;
    private readonly AfdevNativeEvidence evidence;
    private readonly MatchAdmissionRound round;
    private readonly MatchAdmissionLease lease;
    private readonly IPEndPoint admittedEndpoint;
    private readonly AfdevLoginIdentityFilter login;
    private readonly string nickname;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendToTunnel;
    private readonly Action<string>? log;
    private readonly Socket upstream;
    private readonly IPEndPoint upstreamEndpoint;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task receiver;
    private readonly Task observer;
    private readonly Task completion;
    private Task? stopTask;
    private long clientPackets;
    private long nativePackets;
    private bool loggedLogin;
    private bool loggedJoin;

    /// <summary>
    /// Accepts only current eligibility from the same allocation and strict loopback readiness. Successful
    /// construction owns an exclusive upstream and sole receiver.
    /// </summary>
    public AfdevLeaseRelay(AfdevProcessHost host, MatchAdmissionRound round, MatchAdmissionLease lease,
        string nickname, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendToTunnel, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sendToTunnel);

        evidence = host.ReadyEvidence ?? throw new InvalidOperationException("AFDEV 尚未严格就绪，不能创建 relay。");
        admittedEndpoint = lease.RemoteEndpoint;

        if (host.State != AfdevRuntimeState.SessionReady || evidence.AllocationId != round.AllocationId ||
            host.Binding is not { } binding || binding.AllocationId != round.AllocationId ||
            binding.InstanceId != evidence.InstanceId || binding.AttemptId != evidence.AttemptId ||
            binding.WorldGeneration != evidence.WorldGeneration ||
            lease.AllocationId != round.AllocationId || evidence.NativeSocketAddress != "127.0.0.1" ||
            evidence.NativeSocketPort != evidence.TargetPort || !round.IsCurrent(lease, admittedEndpoint))
            throw new InvalidOperationException("relay 必须使用当前精确 allocation/lease 和 loopback 原生 socket。");

        this.host = host;
        this.round = round;
        this.lease = lease;
        this.sendToTunnel = sendToTunnel;
        this.log = log;
        this.nickname = nickname;
        login = new AfdevLoginIdentityFilter(lease.Uin, nickname,
            AfdevMapCatalog.UsesTeamSeats(evidence.ModeId) ? TeamDeathmatchSeats.Camp(lease.SeatIndex) :
                evidence.ModeId == 0x1004 ? (byte)0 : null, lease.Observer);
        upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
            Blocking = false
        };

        try
        {
            upstream.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            upstream.Connect(new IPEndPoint(IPAddress.Loopback, evidence.TargetPort));
            upstreamEndpoint = (IPEndPoint)upstream.LocalEndPoint!;
            receiver = ReceiveAsync();
            observer = ObservePlayerAsync();
            completion = Task.WhenAll(receiver, observer);
        }
        catch
        {
            upstream.Dispose();
            stopping.Dispose();

            throw;
        }

        Report($"RELAY_CREATED upstream={UpstreamEndpoint} target=127.0.0.1:{evidence.TargetPort}");
    }

    /// <summary>
    /// This lease's exclusive native source endpoint, usable for read-only NetConnection association, not
    /// as another authentication entry point.
    /// </summary>
    public IPEndPoint UpstreamEndpoint => new(upstreamEndpoint.Address, upstreamEndpoint.Port);

    /// <summary>
    /// The current native receive task. Shutdown waits for it so old-round callbacks cannot survive.
    /// </summary>
    public Task Completion => completion;

    /// <summary>Datagrams submitted to this native socket.</summary>
    public long ClientDatagramCount => Interlocked.Read(ref clientPackets);

    /// <summary>Datagrams returned by this native socket and delivered to the exact channel.</summary>
    public long NativeDatagramCount => Interlocked.Read(ref nativePackets);

    /// <summary>Actual I/O or identity-filter failure. The lease is irreversibly revoked on failure.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// Submits a datagram from the exact TLS channel without endpoint migration. Sends nothing without
    /// readiness and eligibility.
    /// </summary>
    public void ForwardClientDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty || datagram.Length > 65507)
            throw new InvalidDataException("CONNECT 原生数据报超出 UDP 范围。");

        byte[] supplied = datagram.ToArray();

        try
        {
            bool accepted = round.TryUseLease(lease, admittedEndpoint, () =>
            {
                lock (gate)
                {
                    if (stopping.IsCancellationRequested || host.State != AfdevRuntimeState.SessionReady ||
                        host.ReadyEvidence is not { } current || current.AllocationId != evidence.AllocationId ||
                        current.InstanceId != evidence.InstanceId || current.WorldGeneration != evidence.WorldGeneration ||
                        current.AttemptId != evidence.AttemptId || current.NativeSocketAddress != "127.0.0.1")
                        throw new InvalidOperationException("本次 AFDEV readiness 已失效。");

                    byte[] wire = login.Inspect(supplied);

                    // Nonblocking Send commits and Revoke share one round critical section. OS backpressure fails directly instead of queuing old-round packets.
                    if (upstream.Send(wire, SocketFlags.None) != wire.Length)
                        throw new IOException("原生 UDP 未完整提交。");

                    Interlocked.Increment(ref clientPackets);

                    if (login.LoginObserved && !loggedLogin)
                    {
                        loggedLogin = true;
                        Report($"NATIVE_LOGIN_URL_BOUND upstream={UpstreamEndpoint} uin={lease.Uin}");
                    }
                    if (login.JoinObserved && !loggedJoin)
                    {
                        loggedJoin = true;
                        Report($"NATIVE_JOIN_FORWARDED upstream={UpstreamEndpoint} uin={lease.Uin}");
                    }
                }
            });

            // Departure may revoke the round after adapter validation. Discard already in-flight same-round packets without recording normal revocation as native failure.
            if (!accepted) return;
        }
        catch (Exception failure)
        {
            Fail(failure);

            throw;
        }
    }

    /// <summary>
    /// Revokes idempotently and waits for the sole receive task. Never await while holding the lobby or
    /// round lock.
    /// </summary>
    public Task StopAsync()
    {
        // Do not acquire the relay lock first; preserve one-way round -> relay lock ordering.
        round.TryUseLease(lease, admittedEndpoint, lease.Revoke);

        lock (gate)
        {
            if (stopTask != null) return stopTask;
            if (!stopping.IsCancellationRequested) stopping.Cancel();

            return stopTask = StopCoreAsync();
        }
    }

    /// <summary>Reclaims this object's resources in shutdown order.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StopCoreAsync()
    {
        await completion.ConfigureAwait(false);
        upstream.Dispose();
        stopping.Dispose();
        Report($"RELAY_STOPPED client_packets={ClientDatagramCount} native_packets={NativeDatagramCount}");
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[65507];

        try
        {
            while (true)
            {
                int length = await upstream.ReceiveAsync(buffer, SocketFlags.None, stopping.Token).ConfigureAwait(false);

                if (!round.IsCurrent(lease, admittedEndpoint) || stopping.IsCancellationRequested)
                    return;

                // The CONNECT write lock may apply backpressure; do not await it under the round lock. The exact-channel callback rechecks the lease at actual write time,
                // and Connect also checks allocation/admission IDs, so late completion cannot switch to a new round.
                await sendToTunnel(buffer.AsMemory(0, length), stopping.Token).ConfigureAwait(false);
                Interlocked.Increment(ref nativePackets);

                if (NativeDatagramCount == 1)
                    Report($"NATIVE_FIRST_RETURN upstream={UpstreamEndpoint} bytes={length}");
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception failure) { Fail(failure); }
    }

    private async Task ObservePlayerAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token).ConfigureAwait(false);

                if (!round.IsCurrent(lease, admittedEndpoint)) return;

                var player = await host.ReadPlayerEvidenceAsync(upstreamEndpoint, lease.Uin, nickname, stopping.Token).ConfigureAwait(false);

                if (player != null && round.IsCurrent(lease, admittedEndpoint))
                    Report("NATIVE_PLAYER_OBSERVED " + global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(player));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception failure) { Fail(failure); }
    }

    private void Fail(Exception failure)
    {
        round.TryUseLease(lease, admittedEndpoint, lease.Revoke);

        lock (gate)
        {
            Failure ??= failure;
            if (!stopping.IsCancellationRequested) stopping.Cancel();
        }

        Report($"RELAY_FAILED stage={(loggedLogin ? "post-login" : "handshake")} exception={failure.GetType().Name} message={failure.Message}");
    }

    private void Report(string message)
    {
        try { log?.Invoke($"[{evidence.AllocationId:N}/{evidence.AttemptId:N}] {message}"); }
        catch (Exception) { }
    }
}
