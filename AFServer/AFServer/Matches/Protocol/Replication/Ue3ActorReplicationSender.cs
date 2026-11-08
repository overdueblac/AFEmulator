namespace AFServer.AssaultFire.Matches.Protocol;

public enum Ue3ActorSendState { OpenPending, Open, UpdatePending, ClosePending, Closed, Aborted }

/// <summary>
/// ActorId is caller-owned world identity; SourceObjectId is the static identity of an effective
/// archetype/existing Actor.
/// </summary>
public sealed record Ue3ActorSendRequest(long ActorId, long SourceObjectId,
    Ue3QuantizedVector? InitialLocation, Ue3Rotator? InitialRotation, byte? PlayerControllerIndex);

public sealed record Ue3ActorSendChannel(int ChannelIndex, long ActorId, long SourceObjectId,
    long Revision, Ue3ActorSendState State, long? PendingReliableId);

/// <summary>
/// Single-connection, single-thread Actor sender. Public construction exclusively owns its reliable peer;
/// internal sharing uses unified connection scheduling only. Accepts trusted world-selected properties and
/// sender-owned dynamic references, without creating Actors or executing client RPCs. Channel type 2
/// follows existing Actor bunch rules, with one unacknowledged bunch per channel. ACKs advance local send
/// state only. Without a cross-channel generation barrier, PackageMap Revision changes after first open
/// terminate the peer and discard retransmissions. Unified connections likewise support no cross-generation
/// migration or world construction. Synchronous field/provider callbacks cannot reenter send/receive/close;
/// read-only channel queries remain available.
/// </summary>
public sealed class Ue3ActorReplicationSender
{
    private sealed class Entry(int channel, Ue3ActorSendRequest request, long revision, long pending,
        long classObjectId, HashSet<int> references)
    {
        internal readonly int Channel = channel;
        internal readonly Ue3ActorSendRequest Request = request;
        internal readonly long Revision = revision;
        internal readonly long ClassObjectId = classObjectId;
        internal readonly Ue3ActorReference Reference = new(channel, request.ActorId);
        internal Ue3ActorSendState State = Ue3ActorSendState.OpenPending;
        internal long? Pending = pending;
        internal HashSet<int> PendingReferences = references;
    }

    private readonly Ue3ReplicationContext context;
    private readonly Ue3ReliablePeer peer;
    private readonly bool ownsPeer;
    private readonly Dictionary<int, Entry> channels = [];
    private readonly HashSet<long> actorIds = [];
    private Ue3ReplicationSnapshot? pinned;
    private bool operationInProgress;

    public bool IsDisconnected => peer.IsDisconnected;
    public string? DisconnectReason => peer.DisconnectReason;
    public int PendingReliableCount => peer.PendingReliableCount;

    public Ue3ActorReplicationSender(Ue3ReplicationContext context, Ue3ReliabilityOptions? options = null,
        long nowMilliseconds = 0, int initialPacketId = 0)
        : this(context, new Ue3ReliablePeer(options, nowMilliseconds, initialPacketId), true)
    {
    }

    internal Ue3ActorReplicationSender(Ue3ReplicationContext context, Ue3ReliablePeer peer)
        : this(context, peer, false)
    {
    }

    private Ue3ActorReplicationSender(Ue3ReplicationContext context, Ue3ReliablePeer peer, bool ownsPeer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(peer);
        this.context = context;
        this.peer = peer;
        this.ownsPeer = ownsPeer;
    }

    public Ue3ActorSendChannel GetChannel(int channelIndex)
    {
        var entry = Find(channelIndex);

        return new(entry.Channel, entry.Request.ActorId, entry.Request.SourceObjectId,
            entry.Revision, entry.State, entry.Pending);
    }

    /// <summary>
    /// Gets an Actor reference after this connection's open ACK. It remains referenceable while property
    /// updates await ACK, but not after close begins. The open ACK confirms transport only; trusted callers
    /// still own world liveness, relevance, and replication conditions.
    /// </summary>
    public Ue3ActorReference GetActorReference(int channelIndex)
    {
        using var operation = BeginOperation();
        var entry = Find(channelIndex);

        RequireReferenceable(entry);
        _ = RequireSnapshot();
        return entry.Reference;
    }

    /// <summary>
    /// Registers channels only after complete encoding/final packet-length checks. Never reuses channel
    /// numbers or Actor identities within a connection.
    /// </summary>
    public long QueueOpen(int channelIndex, Ue3ActorSendRequest request, IReadOnlyList<Ue3ActorFieldUpdate> fields)
    {
        using var operation = BeginOperation();

        ArgumentNullException.ThrowIfNull(request);
        if (channelIndex is <= 0 or >= Ue3PacketCodec.ChannelMaximum || request.ActorId <= 0
            || channels.ContainsKey(channelIndex) || actorIds.Contains(request.ActorId))
            throw new InvalidDataException("Actor 通道或世界身份无效、重复，或尝试复用已关闭身份。");

        var snapshot = RequireSnapshot();
        var open = snapshot.GetActorOpenContext(request.SourceObjectId);
        var schema = snapshot.GetSchema(open.ActorClassObjectId);
        var resolved = ResolveFields(snapshot, schema, fields, out var references);
        var writer = new LsbBitWriter(4095);
        var header = new Ue3ActorOpenHeader(open.ObjectReference, request.InitialLocation,
            request.InitialRotation, request.PlayerControllerIndex);

        Ue3ActorOpenCodec.WritePayload(writer, true, open, schema, new(header, resolved));

        byte[] bytes = writer.ToArray();

        // Actual packet headers consume space too; payload <= 512 alone does not establish sendability.
        _ = Ue3PacketCodec.Encode(new(0, [new Ue3Bunch(true, true, false, true,
            channelIndex, 1, 2, writer.Position, bytes)]));
        RequireSharedCapacity(bytes.Length);
        peer.RegisterChannel(channelIndex, 2, Ue3ChannelStart.AwaitRemoteOpen);
        RequireConnected();

        long id = peer.QueueReliable(channelIndex, bytes, writer.Position, open: true);

        RequireConnected();
        channels.Add(channelIndex, new(channelIndex, request, snapshot.Revision, id, open.ActorClassObjectId, references));
        actorIds.Add(request.ActorId);
        pinned ??= snapshot;
        return id;
    }

    public long QueueProperties(int channelIndex, IReadOnlyList<Ue3ActorFieldUpdate> fields)
    {
        using var operation = BeginOperation();
        var snapshot = RequireSnapshot();
        var entry = FindOpen(channelIndex);
        var open = snapshot.GetActorOpenContext(entry.Request.SourceObjectId);
        var schema = snapshot.GetSchema(open.ActorClassObjectId);
        var resolved = ResolveFields(snapshot, schema, fields, out var references);
        var writer = new LsbBitWriter(4095);

        Ue3ActorReplicationCodec.WriteExistingActorFields(writer, schema, resolved);

        byte[] bytes = writer.ToArray();

        _ = Ue3PacketCodec.Encode(new(0, [new Ue3Bunch(false, false, false, true,
            channelIndex, 1, 2, writer.Position, bytes)]));
        RequireSharedCapacity(bytes.Length);

        long id = peer.QueueReliable(channelIndex, bytes, writer.Position);

        RequireConnected();
        entry.Pending = id;
        entry.PendingReferences = references;
        entry.State = Ue3ActorSendState.UpdatePending;
        return id;
    }

    /// <summary>
    /// Queues empty close only after ACKs for the prior reliable send and in-flight references from other
    /// channels. Cross-channel reliability is unordered; waiting prevents close preceding references or
    /// old-reference retransmission. Retain a tombstone after close.
    /// </summary>
    public long QueueClose(int channelIndex)
    {
        using var operation = BeginOperation();

        _ = RequireSnapshot();

        var entry = FindOpen(channelIndex);

        if (channels.Values.Any(other => other.PendingReferences.Contains(channelIndex)))
            throw new InvalidOperationException("仍有未确认的 Actor 属性引用此通道；等待引用发送确认后再关闭。");
        RequireSharedCapacity(0);

        long id = peer.QueueReliable(channelIndex, [], 0, close: true);

        RequireConnected();
        entry.Pending = id;
        entry.State = Ue3ActorSendState.ClosePending;
        return id;
    }

    public Ue3PeerUpdate Tick(long nowMilliseconds)
    {
        using var operation = BeginOperation();

        RequireOwnedPeer();
        if (!CheckSnapshot()) return FailedUpdate();
        return Process(peer.Tick(nowMilliseconds));
    }

    /// <summary>
    /// Consumes only ACKs and empty reliable remote closes. Client fields/RPCs terminate the connection
    /// because no authenticated command adapter exists yet.
    /// </summary>
    public Ue3PeerUpdate Receive(ReadOnlySpan<byte> plaintext, long nowMilliseconds)
    {
        using var operation = BeginOperation();

        RequireOwnedPeer();
        if (!CheckSnapshot()) return FailedUpdate();
        return Process(peer.Receive(plaintext, nowMilliseconds));
    }

    public void Disconnect(string reason = "local-close")
    {
        using var operation = BeginOperation();

        DisconnectCore(reason);
    }

    private void DisconnectCore(string reason)
    {
        peer.Disconnect(reason);
        foreach (var entry in channels.Values)
        {
            if (entry.State != Ue3ActorSendState.Closed) entry.State = Ue3ActorSendState.Aborted;
            entry.Pending = null;
            entry.PendingReferences.Clear();
        }
    }

    private Ue3PeerUpdate Process(Ue3PeerUpdate update)
    {
        if (update.IsDisconnected)
        {
            DisconnectCore(update.DisconnectReason ?? "transport-failure");
            return FailedUpdate();
        }
        // Validate the full delivery batch before confirming local state. On failure, never expose packets already flushed by the peer.
        try { ValidateDelivered(update.Delivered); }
        catch (InvalidDataException)
        {
            DisconnectCore("unsupported-client-actor-data");
            return FailedUpdate();
        }
        if (HasPendingReferencesToClosedChannels(update.AcknowledgedReliableIds, update.Delivered))
        {
            DisconnectCore("referenced-actor-closed");
            return FailedUpdate();
        }
        ApplyAcknowledgedAndDelivered(update.AcknowledgedReliableIds, update.Delivered);
        return update;
    }

    /// <summary>
    /// Side-effect-free preflight. Unified connections validate all control/Actor messages before advancing
    /// any application state.
    /// </summary>
    internal void ValidateDelivered(IReadOnlyList<Ue3Bunch> delivered)
    {
        foreach (var bunch in delivered)
            if (!channels.TryGetValue(bunch.ChannelIndex, out var entry) || bunch.Open || !bunch.Close
                || !bunch.Reliable || bunch.ChannelType != 2 || bunch.PayloadBitLength != 0
                || entry.State == Ue3ActorSendState.Aborted)
                throw new InvalidDataException("客户端 Actor 通道只允许已登记通道的空可靠 close。");
    }

    /// <summary>
    /// If the peer closes an in-flight reference target, terminate the whole connection to retract
    /// unreturned retransmissions. Source channels acknowledged/closed in the same batch have no pending
    /// references. Check before ACK publication/unified flush without mutating state.
    /// </summary>
    internal bool HasPendingReferencesToClosedChannels(IReadOnlyList<long> acknowledged,
        IReadOnlyList<Ue3Bunch> delivered)
    {
        var closed = delivered.Select(bunch => bunch.ChannelIndex).ToHashSet();

        if (closed.Count == 0) return false;

        var confirmed = acknowledged.ToHashSet();

        return channels.Values.Any(entry => entry.Pending is long id && !confirmed.Contains(id)
            && !closed.Contains(entry.Channel) && entry.PendingReferences.Overlaps(closed));
    }

    internal void ApplyAcknowledgedAndDelivered(IReadOnlyList<long> acknowledged, IReadOnlyList<Ue3Bunch> delivered)
    {
        foreach (long id in acknowledged)
            foreach (var entry in channels.Values)
                if (entry.Pending == id)
                {
                    entry.Pending = null;
                    entry.PendingReferences.Clear();
                    entry.State = entry.State == Ue3ActorSendState.ClosePending
                        ? Ue3ActorSendState.Closed : Ue3ActorSendState.Open;
                }
        foreach (var bunch in delivered)
        {
            var entry = channels[bunch.ChannelIndex];

            entry.Pending = null; // Remote close cancels peer pending entries without supplying their ACK IDs.
            entry.PendingReferences.Clear();
            entry.State = Ue3ActorSendState.Closed;
        }
    }

    // Before open, negotiation-not-ready is normal. Missing replication snapshots must not terminate control negotiation.
    internal bool CheckPinnedSnapshot()
    {
        using var operation = BeginOperation();

        return pinned is null ? !IsDisconnected : CheckSnapshot();
    }

    private void RequireSharedCapacity(int payloadBytes)
    {
        if (!ownsPeer && (peer.AvailableReliableSlots == 0 || payloadBytes > peer.AvailableReliableBytes))
            throw new InvalidOperationException("共享可靠发送预算已满；等待 ACK 后重试，尚未占用通道或身份。");
    }

    private void RequireOwnedPeer()
    {
        if (!ownsPeer) throw new InvalidOperationException("共享传输只能由统一连接调用 Tick/Receive。");
    }

    private Ue3ActorFieldUpdate[] ResolveFields(Ue3ReplicationSnapshot snapshot,
        Ue3ActorReplicationSchema schema, IReadOnlyList<Ue3ActorFieldUpdate> fields, out HashSet<int> references)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count > Ue3ActorReplicationSchema.MaximumEntries)
            throw new InvalidDataException("Actor 字段流条目数超限。");

        var resolved = new Ue3ActorFieldUpdate[fields.Count];

        references = [];
        for (int i = 0; i < resolved.Length; i++)
        {
            if (fields[i] is not Ue3ActorPropertyUpdate property || property.Value is Ue3ObjectReferenceValue)
                throw new InvalidDataException("发送层仅接受已选定的属性；原始 wire 对象引用和 RPC 未获授权。");
            if (property.Value is not Ue3ActorReferenceValue value)
            {
                resolved[i] = property;
                continue;
            }

            var definition = schema.GetProperty(property.PropertyObjectId);

            if (definition.ValueKind != Ue3ActorValueKind.ObjectReference ||
                definition.ReferencedClassObjectId is not long requiredClass)
                throw new InvalidDataException("发送对象属性前必须提供可信的 ObjectProperty 目标类声明。");

            var wireReference = Ue3ObjectReference.Null;

            if (value.Reference is { } reference)
            {
                if (!channels.TryGetValue(reference.ChannelIndex, out var target) ||
                    !ReferenceEquals(target.Reference, reference))
                    throw new InvalidDataException("Actor 引用不属于本发送连接登记的通道。");
                RequireReferenceable(target);
                if (!snapshot.IsClassOrSubclassOf(target.ClassObjectId, requiredClass))
                    throw new InvalidDataException("Actor 实际类不符合对象属性的目标类声明。");
                wireReference = new(Ue3ObjectReferenceKind.ActorChannel, target.Channel);
                references.Add(target.Channel);
            }
            // Native-reference format and ObjectProperty target-class checks are referenced in object-reference-evidence.md.
            // Use registered channels here. Archetype flags are not actual instance flags; world relevance remains caller-owned.
            resolved[i] = property with { Value = new Ue3ObjectReferenceValue(wireReference) };
        }
        return resolved;
    }

    private static void RequireReferenceable(Entry entry)
    {
        if (entry.State is not (Ue3ActorSendState.Open or Ue3ActorSendState.UpdatePending))
            throw new InvalidOperationException("引用的 Actor 尚未确认 open，或通道已开始关闭。");
    }

    private Ue3ReplicationSnapshot RequireSnapshot()
    {
        RequireConnected();

        Ue3ReplicationSnapshot current;

        try { current = context.CaptureSnapshot(); }
        catch
        {
            DisconnectCore("replication-context-unavailable");
            throw;
        }
        if (pinned is not null && !ReferenceEquals(pinned, current))
        {
            DisconnectCore("replication-revision-changed");
            throw new InvalidOperationException("已有 Actor 通道时包快照发生变化；尚无可安全复用的跨通道代际屏障。");
        }
        RequireConnected();
        return current;
    }

    private bool CheckSnapshot()
    {
        if (IsDisconnected) return false;
        try { _ = RequireSnapshot(); return true; }
        catch (Exception exception) when (IsDisconnected && exception is
            InvalidDataException or InvalidOperationException or ArgumentException) { return false; }
    }

    private void RequireConnected()
    {
        if (!peer.IsDisconnected) return;
        DisconnectCore(peer.DisconnectReason ?? "transport-failure");
        throw new InvalidOperationException("Actor 发送连接已终止。");
    }

    private Entry Find(int channelIndex) => channels.TryGetValue(channelIndex, out var entry)
        ? entry : throw new InvalidDataException("Actor 通道尚未登记。");

    private Entry FindOpen(int channelIndex)
    {
        var entry = Find(channelIndex);

        return entry.State == Ue3ActorSendState.Open ? entry
            : throw new InvalidOperationException("Actor 通道尚未确认、仍有待确认发送或已关闭。");
    }

    private Ue3PeerUpdate FailedUpdate() => new([], [], [], true, DisconnectReason);

    private Operation BeginOperation()
    {
        if (operationInProgress) throw new InvalidOperationException("Actor 发送器不允许同步操作重入。");
        operationInProgress = true;
        return new(this);
    }

    private readonly struct Operation(Ue3ActorReplicationSender owner) : IDisposable
    {
        public void Dispose() => owner.operationInProgress = false;
    }
}
