using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Plaintext output for one update. Connection-wide ACK IDs do not prove package Have, Actor business
/// execution, or authentication.
/// </summary>
public sealed record Ue3ReplicationConnectionUpdate(IReadOnlyList<byte[]> Packets,
    IReadOnlyList<long> AcknowledgedReliableIds, long Revision, bool IsReady,
    bool IsDisconnected, string? DisconnectReason);

/// <summary>
/// Single-connection, single-thread composer after trusted control verification. Control/Actor share one
/// peer, PacketId, delivery IDs, and budget. Trusted callers supply physical packages, metadata, control
/// ordinals, and world-Actor selection. Performs no Hello/Login, UDP/AES, construction, or RPC;
/// construction does not authenticate players. First open fixes replication revision. Control revision
/// changes/unloads terminate the connection without migrating in-flight data. Scheduling/enqueue reject
/// synchronous reentry so collection callbacks cannot bypass reference-dependency checks.
/// </summary>
public sealed class Ue3ReplicationConnection
{
    private readonly Ue3ReliablePeer peer;
    private readonly Ue3PackageNegotiationSession control;
    private readonly Ue3ReplicationContext context;
    private readonly Ue3ActorReplicationSender actors;
    private bool operationInProgress;

    public long Revision => control.Revision;
    public bool IsReady => control.IsReady;
    public bool IsDisconnected => peer.IsDisconnected;
    public string? DisconnectReason => peer.DisconnectReason;
    public int PendingReliableCount => peer.PendingReliableCount;
    public int QueuedControlMessageCount => control.QueuedControlMessageCount;

    /// <summary>
    /// Control starts come from the same verified session. Accepts no other peer's in-flight packets and
    /// infers no ordinal from untrusted first packets. Return all new output through this object, never
    /// mixed with an old peer. Construction sends nothing. A peer that exchanged handshake traffic must
    /// transfer through ContinueFrom rather than reset history here.
    /// </summary>
    public Ue3ReplicationConnection(IReadOnlyList<Ue3PackageNegotiationPackage> packages,
        IReadOnlyList<Ue3ReplicationClassDefinition> classes, IReadOnlyList<Ue3ReplicationObjectDefinition> objects,
        Ue3PackageNegotiationSessionOptions options, Ue3ReliabilityOptions reliability,
        long nowMilliseconds, int nextIncomingControlSequence, int nextOutgoingControlSequence, int initialPacketId = 0)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        peer = new(reliability, nowMilliseconds, initialPacketId);
        control = new(packages, options, reliability, peer, nowMilliseconds,
            nextIncomingControlSequence, nextOutgoingControlSequence);
        context = new(control.CaptureSnapshot, classes, objects);
        actors = new(context, peer);
    }

    /// <summary>
    /// Consumes complete reliable state transferred by the handshake owner, without resetting packet
    /// numbers, control registration, or receive-idle time. Construction attempts consume the token;
    /// validation failure closes it without restoring the old owner. Does not authenticate players; trusted
    /// callers still associate membership, session, and endpoint.
    /// </summary>
    public static Ue3ReplicationConnection ContinueFrom(Ue3ReliableControlHandoff handoff,
        IReadOnlyList<Ue3PackageNegotiationPackage> packages, IReadOnlyList<Ue3ReplicationClassDefinition> classes,
        IReadOnlyList<Ue3ReplicationObjectDefinition> objects, Ue3PackageNegotiationSessionOptions options,
        long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        var transferredPeer = handoff.Consume();

        try
        {
            transferredPeer.AdvanceHandoffClock(nowMilliseconds);
            return new(packages, classes, objects, options, transferredPeer, nowMilliseconds,
                handoff.NextIncomingSequence, handoff.NextOutgoingSequence);
        }
        catch
        {
            transferredPeer.Disconnect("replication-handoff-failed");
            throw;
        }
    }

    private Ue3ReplicationConnection(IReadOnlyList<Ue3PackageNegotiationPackage> packages,
        IReadOnlyList<Ue3ReplicationClassDefinition> classes, IReadOnlyList<Ue3ReplicationObjectDefinition> objects,
        Ue3PackageNegotiationSessionOptions options, Ue3ReliablePeer peer, long nowMilliseconds,
        int nextIncomingControlSequence, int nextOutgoingControlSequence)
    {
        this.peer = peer;
        control = new(packages, options, peer.ReliabilityOptions, peer, nowMilliseconds,
            nextIncomingControlSequence, nextOutgoingControlSequence, controlAlreadyRegistered: true);
        context = new(control.CaptureSnapshot, classes, objects);
        actors = new(context, peer);
    }

    public Ue3PackageNegotiationSnapshot CapturePackageSnapshot() => control.CaptureSnapshot();
    public Ue3ReplicationSnapshot CaptureSnapshot() => context.CaptureSnapshot();
    public Ue3ActorSendChannel GetChannel(int channelIndex) => actors.GetChannel(channelIndex);

    /// <summary>
    /// Gets this unified connection's open-confirmed Actor reference, rechecking type/channel lifetime
    /// whenever sending an object property.
    /// </summary>
    public Ue3ActorReference GetActorReference(int channelIndex)
    {
        using var operation = BeginOperation();

        RequireReady();
        return actors.GetActorReference(channelIndex);
    }

    public long QueueOpen(int channelIndex, Ue3ActorSendRequest request, IReadOnlyList<Ue3ActorFieldUpdate> fields)
        => Queue(() => actors.QueueOpen(channelIndex, request, fields));

    public long QueueProperties(int channelIndex, IReadOnlyList<Ue3ActorFieldUpdate> fields)
        => Queue(() => actors.QueueProperties(channelIndex, fields));

    public long QueueClose(int channelIndex) => Queue(() => actors.QueueClose(channelIndex));

    /// <summary>
    /// Performs at most one reliable flush per update. Control and Actor output jointly obey
    /// MaximumPacketsPerUpdate.
    /// </summary>
    public Ue3ReplicationConnectionUpdate Tick(long nowMilliseconds)
    {
        using var operation = BeginOperation();

        if (!Advance(nowMilliseconds)) return Result([], []);
        return Flush(nowMilliseconds, []);
    }

    /// <summary>
    /// Completes reliable delivery and both message preflights first, then applies control semantics,
    /// checks replication revision, handles Actor ACK/close, and flushes together. Failure suppresses all
    /// output/ACKs for this update. Later control-semantic failure disconnects; it does not claim internal
    /// diagnostic Revision rollback.
    /// </summary>
    public Ue3ReplicationConnectionUpdate Receive(ReadOnlySpan<byte> plaintext, long nowMilliseconds)
    {
        using var operation = BeginOperation();

        if (!Advance(nowMilliseconds)) return Result([], []);

        var update = peer.ReceiveDeferred(plaintext, nowMilliseconds);

        if (update.IsDisconnected) return Fail(update.DisconnectReason!);

        var controlBunches = update.Delivered.Where(x => x.ChannelIndex == 0).ToArray();
        var actorBunches = update.Delivered.Where(x => x.ChannelIndex != 0).ToArray();
        IReadOnlyList<Ue3ControlMessage> messages;

        try { messages = control.ValidateDelivered(controlBunches); }
        catch (InvalidDataException) { return Fail("invalid-package-control"); }
        try { actors.ValidateDelivered(actorBunches); }
        catch (InvalidDataException) { return Fail("unsupported-client-actor-data"); }
        if (actors.HasPendingReferencesToClosedChannels(update.AcknowledgedReliableIds, actorBunches))
            return Fail("referenced-actor-closed");
        try { control.ApplyDelivered(messages); }
        catch (InvalidDataException) { return Fail("invalid-package-control"); }

        // Transport may already remove ACKed pending entries, but do not publish success to sender/caller
        // until the same control batch's new revision passes validation. No ACK or retransmission output has been generated yet.
        if (!actors.CheckPinnedSnapshot()) return Fail(DisconnectReason ?? "replication-context-unavailable");
        control.AcknowledgeShared(update.AcknowledgedReliableIds);
        actors.ApplyAcknowledgedAndDelivered(update.AcknowledgedReliableIds, actorBunches);
        return Flush(nowMilliseconds, update.AcknowledgedReliableIds);
    }

    /// <summary>
    /// Unloads under verified control semantics before first Actor open. Connections with existing Actors
    /// terminate immediately without Unload or old-Actor retransmission. ACKs and all-Actor closes are not
    /// proven cross-channel generation barriers.
    /// </summary>
    public Ue3ReplicationConnectionUpdate RequestUnload(Ue3PackageGuid guid, long nowMilliseconds)
    {
        using var operation = BeginOperation();

        if (!Advance(nowMilliseconds)) return Result([], []);
        control.BeginUnload(guid, nowMilliseconds);
        if (!actors.CheckPinnedSnapshot()) return Fail(DisconnectReason ?? "replication-context-unavailable");
        return Flush(nowMilliseconds, []);
    }

    public void Disconnect(string reason = "local-close")
    {
        using var operation = BeginOperation();

        DisconnectCore(reason);
    }

    private void DisconnectCore(string reason)
    {
        control.Disconnect(reason);
        actors.Disconnect(reason);
    }

    private long Queue(Func<long> enqueue)
    {
        using var operation = BeginOperation();

        // Reject premature sends while negotiation is incomplete, retaining the control channel for Have/Skip.
        RequireReady();
        try { return enqueue(); }
        catch
        {
            if (IsDisconnected) DisconnectCore(DisconnectReason!);
            throw;
        }
    }

    private bool Advance(long nowMilliseconds)
    {
        if (!control.AdvanceShared(nowMilliseconds) || !actors.CheckPinnedSnapshot())
        {
            DisconnectCore(DisconnectReason ?? "replication-context-unavailable");
            return false;
        }
        return true;
    }

    private Ue3ReplicationConnectionUpdate Flush(long nowMilliseconds, IReadOnlyList<long> acknowledged)
    {
        control.FillSharedBatch();
        if (IsDisconnected) return Fail(DisconnectReason!);

        var update = peer.Tick(nowMilliseconds);

        if (update.IsDisconnected) return Fail(update.DisconnectReason!);
        control.RecordSharedEmissions(update.Packets);
        return Result(update.Packets, acknowledged);
    }

    private Ue3ReplicationConnectionUpdate Fail(string reason)
    {
        DisconnectCore(reason);
        return Result([], []);
    }

    private Ue3ReplicationConnectionUpdate Result(IReadOnlyList<byte[]> packets, IReadOnlyList<long> acknowledged)
        => new(IsDisconnected ? ImmutableArray<byte[]>.Empty : packets.ToImmutableArray(),
            IsDisconnected ? ImmutableArray<long>.Empty : acknowledged.ToImmutableArray(),
            Revision, IsReady, IsDisconnected, DisconnectReason);

    private void RequireReady()
    {
        if (!IsReady) throw new InvalidOperationException("包协商尚未就绪或连接已终止，不能使用 Actor 发送接口。");
    }

    private Operation BeginOperation()
    {
        if (operationInProgress) throw new InvalidOperationException("统一复制连接不允许同步操作重入。");
        operationInProgress = true;
        return new(this);
    }

    private readonly struct Operation(Ue3ReplicationConnection owner) : IDisposable
    {
        public void Dispose() => owner.operationInProgress = false;
    }
}
