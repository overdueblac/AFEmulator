using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>Server implementation deadlines/parsing budgets, not recovered native-client constants.</summary>
public sealed record Ue3PackageNegotiationSessionOptions
{
    public long InitialNegotiationTimeoutMilliseconds { get; init; } = 30000;
    public long UnloadTimeoutMilliseconds { get; init; } = 10000;
    public int MaximumControlMessagesPerReceive { get; init; } = 1024;

    internal void Validate()
    {
        if (InitialNegotiationTimeoutMilliseconds is < 1 or > 3600000
            || UnloadTimeoutMilliseconds is < 1 or > 3600000
            || MaximumControlMessagesPerReceive is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(Ue3PackageNegotiationSessionOptions));
    }
}

/// <summary>
/// Packets are plaintext awaiting transmission. ACK IDs diagnose transport only, not Have or unload
/// confirmation.
/// </summary>
public sealed record Ue3PackageNegotiationSessionUpdate(IReadOnlyList<byte[]> Packets,
    IReadOnlyList<long> AcknowledgedReliableIds, long Revision, bool IsReady,
    bool IsDisconnected, string? DisconnectReason);

/// <summary>
/// Single-connection, single-thread scheduler after verified control-channel establishment. The caller
/// supplies trusted ordered physical packages, current control ordinals, and monotonic milliseconds, and
/// sends returned plaintext packets. This class owns no Hello/Login, authentication, AES/UDP, or Actor
/// channels. Delivers only reliable channel-0 Have/Skip/Abort; dynamic package addition and reload after
/// unload are unsupported.
/// </summary>
public sealed class Ue3PackageNegotiationSession
{
    private sealed record Command(Ue3PackageGuid Guid, bool IsUnload, byte[] Payload);
    private sealed record InFlight(Command Command, int Sequence);

    private readonly Ue3PackageNegotiation negotiation;
    private readonly Ue3ReliablePeer peer;
    private readonly Ue3PackageNegotiationSessionOptions options;
    private readonly Ue3ReliabilityOptions reliability;
    private readonly bool shared;
    private readonly Queue<Command> outgoing = [];
    private readonly Dictionary<long, InFlight> inFlight = [];
    private readonly HashSet<Ue3PackageGuid> announced = [];
    private readonly HashSet<Ue3PackageGuid> unloadSent = [];
    private readonly Dictionary<Ue3PackageGuid, long> pendingUnload = [];
    private readonly long startedAt;
    private long lastNow;
    private int nextOutgoingSequence;
    private bool initialComplete;
    private Ue3PackageNegotiationSnapshot? snapshot;

    public long Revision => negotiation.Revision;
    public bool IsDisconnected => peer.IsDisconnected;
    public string? DisconnectReason => peer.DisconnectReason;
    /// <summary>
    /// Negotiation is complete with no pending unload, without implying class caches, object slots, map
    /// readiness, or player authorization.
    /// </summary>
    public bool IsReady => !IsDisconnected && negotiation.CanCaptureSnapshot && pendingUnload.Count == 0;
    public bool CanCaptureSnapshot => IsReady;
    public int PendingReliableCount => peer.PendingReliableCount;
    public int QueuedControlMessageCount => outgoing.Count;

    /// <summary>
    /// Both control ordinals must come from the same verified connection. Never infer defaults from an
    /// arbitrary first packet at this stage. Construction validates/stores only; first Tick/Receive sends
    /// Uses.
    /// </summary>
    public Ue3PackageNegotiationSession(IReadOnlyList<Ue3PackageNegotiationPackage> orderedPackages,
        Ue3PackageNegotiationSessionOptions options, Ue3ReliabilityOptions reliability,
        long nowMilliseconds, int nextIncomingControlSequence, int nextOutgoingControlSequence, int initialPacketId = 0)
        : this(orderedPackages, options, reliability, new Ue3ReliablePeer(reliability, nowMilliseconds, initialPacketId),
            nowMilliseconds, nextIncomingControlSequence, nextOutgoingControlSequence, shared: false) { }

    /// <summary>
    /// Only the connection owner calls shared-instance stage methods. Channel 0 remains registered once.
    /// </summary>
    internal Ue3PackageNegotiationSession(IReadOnlyList<Ue3PackageNegotiationPackage> orderedPackages,
        Ue3PackageNegotiationSessionOptions options, Ue3ReliabilityOptions reliability, Ue3ReliablePeer peer,
        long nowMilliseconds, int nextIncomingControlSequence, int nextOutgoingControlSequence,
        bool controlAlreadyRegistered = false)
        : this(orderedPackages, options, reliability, peer, nowMilliseconds,
            nextIncomingControlSequence, nextOutgoingControlSequence, shared: true, controlAlreadyRegistered) { }

    private Ue3PackageNegotiationSession(IReadOnlyList<Ue3PackageNegotiationPackage> orderedPackages,
        Ue3PackageNegotiationSessionOptions options, Ue3ReliabilityOptions reliability, Ue3ReliablePeer peer,
        long nowMilliseconds, int nextIncomingControlSequence, int nextOutgoingControlSequence, bool shared,
        bool controlAlreadyRegistered = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(peer);
        options.Validate();
        reliability.Validate();
        if (nowMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        this.options = options;
        this.reliability = reliability;
        this.peer = peer;
        this.shared = shared;
        negotiation = new(orderedPackages);
        nextOutgoingSequence = nextOutgoingControlSequence;
        startedAt = lastNow = nowMilliseconds;
        initialComplete = negotiation.CanCaptureSnapshot;
        foreach (var announcement in negotiation.CaptureAnnouncements())
        {
            byte[] payload = Ue3ControlCodec.EncodePackageAnnouncement(announcement);

            // The codec's 512-byte string budget excludes bunch/packet headers; this layer performs no unverified fragmentation.
            _ = Ue3PacketCodec.EncodeControlPacket(0, 1, payload);
            if (payload.Length > reliability.MaximumBufferedBytes)
                throw new ArgumentException("单条 Uses 超过配置的可靠发送字节预算。", nameof(reliability));
            outgoing.Enqueue(new(announcement.Guid, false, payload));
        }
        // Unload is exactly 17 bytes. Reject construction settings that cannot support advertised operations.
        if (negotiation.PackageCount > 0 && reliability.MaximumBufferedBytes < 17)
            throw new ArgumentException("可靠发送字节预算无法容纳 Unload。", nameof(reliability));
        if (!controlAlreadyRegistered)
            peer.RegisterChannel(0, 1, Ue3ChannelStart.OpenAlreadyVerified,
                nextIncomingControlSequence, nextOutgoingControlSequence);
    }

    public Ue3PackageNegotiationSessionUpdate Tick(long nowMilliseconds)
    {
        RequireExclusive();
        if (!AdvanceShared(nowMilliseconds)) return Result([], []);

        var packets = new List<byte[]>();

        FillSharedBatch();
        Observe(peer.Tick(nowMilliseconds), packets, []);
        return Result(packets, []);
    }

    public Ue3PackageNegotiationSessionUpdate Receive(ReadOnlySpan<byte> plaintext, long nowMilliseconds)
    {
        RequireExclusive();
        if (!AdvanceShared(nowMilliseconds)) return Result([], []);

        var packets = new List<byte[]>();
        var acknowledged = new List<long>();
        var update = peer.ReceiveDeferred(plaintext, nowMilliseconds);

        if (update.IsDisconnected) { Disconnect(update.DisconnectReason!); return Result([], []); }
        if (update.Delivered.Any(bunch => bunch.Close)) { Disconnect("remote-control-close"); return Result([], []); }
        try
        {
            ApplyDelivered(ValidateDelivered(update.Delivered));
        }
        catch (InvalidDataException)
        {
            // Semantic failure may follow valid messages. Terminate the entire connection and discard output/snapshots without exposing partial success.
            Disconnect("invalid-package-control");
            return Result([], []);
        }
        AcknowledgeShared(update.AcknowledgedReliableIds);
        acknowledged.AddRange(update.AcknowledgedReliableIds);
        FillSharedBatch();
        Observe(peer.Tick(nowMilliseconds), packets, acknowledged);
        return Result(packets, acknowledged);
    }

    /// <summary>
    /// Immediately revokes current replication eligibility. Reliable ACKs cannot end this wait; only a
    /// delivered matching Have/Abort can.
    /// </summary>
    public Ue3PackageNegotiationSessionUpdate RequestUnload(Ue3PackageGuid guid, long nowMilliseconds)
    {
        RequireExclusive();
        if (!AdvanceShared(nowMilliseconds)) return Result([], []);
        BeginUnload(guid, nowMilliseconds);
        return Tick(nowMilliseconds);
    }

    /// <summary>
    /// Changes control state and queues only. The owner must complete the generation barrier before
    /// deciding whether to send together.
    /// </summary>
    internal void BeginUnload(Ue3PackageGuid guid, long nowMilliseconds)
    {
        if (!AdvanceShared(nowMilliseconds)) return;
        if (!IsReady) throw new InvalidOperationException("只能从可用协商状态开始一次卸载。");

        var unload = negotiation.RequestUnload(guid);

        pendingUnload.Add(guid, nowMilliseconds);
        outgoing.Enqueue(new(guid, true, Ue3ControlCodec.EncodePackageUnload(unload)));
    }

    /// <summary>
    /// Returns the same immutable instance for the same Revision after checking connection/readiness. Old
    /// snapshots cannot restore disconnected eligibility.
    /// </summary>
    public Ue3PackageNegotiationSnapshot CaptureSnapshot()
    {
        if (!IsReady) throw new InvalidOperationException("包协商未完成、正在卸载或连接已断开。");
        if (snapshot is null || snapshot.Revision != Revision) snapshot = negotiation.CaptureSnapshot();
        return snapshot;
    }

    public void Disconnect(string reason = "local-close")
    {
        peer.Disconnect(reason);
        outgoing.Clear();
        inFlight.Clear();
        pendingUnload.Clear();
        unloadSent.Clear();
        announced.Clear();
        snapshot = null;
    }

    /// <summary>
    /// Batch syntax preflight changes no negotiation, send eligibility, or ACK state, allowing a
    /// multichannel owner to validate everything first.
    /// </summary>
    internal IReadOnlyList<Ue3ControlMessage> ValidateDelivered(IReadOnlyList<Ue3Bunch> bunches)
    {
        var messages = new List<Ue3ControlMessage>();

        foreach (var bunch in bunches)
        {
            if (bunch.ChannelIndex != 0 || bunch.ChannelType != 1 || !bunch.Reliable || bunch.Open || bunch.Close
                || bunch.PayloadBitLength == 0 || (bunch.PayloadBitLength & 7) != 0)
                throw new InvalidDataException("包协商要求非空、整字节、已打开的可靠控制通道。");

            var stream = Ue3ControlCodec.DecodeKnownStream(bunch.Payload);

            if (stream.Status != Ue3ControlDecodeStatus.Complete
                || stream.Messages.Count > options.MaximumControlMessagesPerReceive - messages.Count
                || stream.Messages.Any(x => x is not (Ue3PackageHave or Ue3PackageSkip or Ue3PackageAbort)))
                throw new InvalidDataException("不支持的包协商控制类型或单次交付预算溢出。");
            messages.AddRange(stream.Messages);
        }
        return messages;
    }

    /// <summary>
    /// Semantic failure may follow a valid prefix. The caller must terminate the entire connection and
    /// suppress this batch's output/snapshot.
    /// </summary>
    internal void ApplyDelivered(IReadOnlyList<Ue3ControlMessage> messages)
    {
        foreach (var message in messages)
        {
            var guid = message switch
            {
                Ue3PackageHave have => have.Guid,
                Ue3PackageSkip skip => skip.Guid,
                Ue3PackageAbort abort => abort.Guid,
                _ => throw new InvalidDataException("未知包协商类型。")
            };

            if (!announced.Contains(guid)) throw new InvalidDataException("包尚未发送 Uses，不能接受回应。");

            bool completingUnload = pendingUnload.ContainsKey(guid);

            if (completingUnload && !unloadSent.Contains(guid))
                throw new InvalidDataException("包尚未发送 Unload，不能接受卸载回应。");
            negotiation.ApplyClientMessage(message);
            if (completingUnload)
            {
                pendingUnload.Remove(guid);
                unloadSent.Remove(guid);
            }
        }
        initialComplete |= negotiation.CanCaptureSnapshot;
    }

    internal void AcknowledgeShared(IReadOnlyList<long> reliableIds)
    {
        foreach (long id in reliableIds) inFlight.Remove(id);
    }

    internal bool AdvanceShared(long now)
    {
        if (now < lastNow) throw new ArgumentOutOfRangeException(nameof(now), "必须传入单调递增的毫秒时钟。");
        lastNow = now;
        if (IsDisconnected) return false;
        if (!initialComplete && now - startedAt >= options.InitialNegotiationTimeoutMilliseconds)
            Disconnect("package-negotiation-timeout");
        else if (pendingUnload.Values.Any(start => now - start >= options.UnloadTimeoutMilliseconds))
            Disconnect("package-unload-timeout");
        return !IsDisconnected;
    }

    internal void FillSharedBatch()
    {
        // Wait for a full batch's ACKs before filling the next: this bounds pending count/bytes and reliable-ordinal span while the oldest loss remains unacknowledged.
        // Keep at most 4,096 Uses in this bounded list rather than filling the default 128-entry pending queue at once.
        if (IsDisconnected || inFlight.Count != 0) return;

        int limit = Math.Min(reliability.MaximumSentHistory,
            Math.Min(reliability.MaximumPendingBunches, reliability.ReorderWindow + 1));

        // Actors/control share pending counts, bytes, and history. Retain queued work at capacity instead of invoking the peer-terminating hard-limit path.
        while (inFlight.Count < limit && peer.AvailableReliableSlotsForChannel(0) > 0 && outgoing.TryPeek(out var command)
            && command.Payload.Length <= peer.AvailableReliableBytes)
        {
            long id = peer.QueueReliable(0, command.Payload, command.Payload.Length * 8);

            if (IsDisconnected) { Disconnect(peer.DisconnectReason!); return; }
            outgoing.Dequeue();
            inFlight.Add(id, new(command, nextOutgoingSequence));
            nextOutgoingSequence = (nextOutgoingSequence + 1) & 1023;
        }
    }

    private void Observe(Ue3PeerUpdate update, List<byte[]> packets, List<long> acknowledged)
    {
        if (update.IsDisconnected) { Disconnect(update.DisconnectReason!); return; }
        AcknowledgeShared(update.AcknowledgedReliableIds);
        acknowledged.AddRange(update.AcknowledgedReliableIds);
        RecordSharedEmissions(update.Packets);
        packets.AddRange(update.Packets);
    }

    internal void RecordSharedEmissions(IReadOnlyList<byte[]> packets)
    {
        foreach (byte[] packet in packets)
        {
            // Only Uses/Unload delivered to the caller for sending permit subsequent application responses. QueueReliable alone is not transmission.
            foreach (var bunch in Ue3PacketCodec.Decode(packet).Records.OfType<Ue3Bunch>().Where(x => x.ChannelIndex == 0))
            {
                // Pre-handoff Challenge/Welcome may still retransmit. Do not mistake old-stage control bunches for current Uses.
                var entry = inFlight.Values.SingleOrDefault(x => x.Sequence == bunch.ChannelSequence);

                if (entry is null) continue;

                var command = entry.Command;

                if (command.IsUnload) unloadSent.Add(command.Guid);
                else announced.Add(command.Guid);
            }
        }
    }

    private void RequireExclusive()
    {
        if (shared) throw new InvalidOperationException("共享控制会话必须由连接 owner 统一接收和发送。");
    }

    private Ue3PackageNegotiationSessionUpdate Result(IReadOnlyList<byte[]> packets, IReadOnlyList<long> acknowledged)
        => new(IsDisconnected ? ImmutableArray<byte[]>.Empty : packets.ToImmutableArray(),
            IsDisconnected ? ImmutableArray<long>.Empty : acknowledged.ToImmutableArray(),
            Revision, IsReady, IsDisconnected, DisconnectReason);
}
