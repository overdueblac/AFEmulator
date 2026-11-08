namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Capacity, timeout, and retransmission settings are server policies, not recovered client constants.
/// </summary>
public sealed record Ue3ReliabilityOptions
{
    public int MaximumChannels { get; init; } = 64;
    public int MaximumPendingBunches { get; init; } = 128;
    public int MaximumBufferedBunches { get; init; } = 128;
    public int MaximumBufferedBytes { get; init; } = 65536;
    public int ReorderWindow { get; init; } = 128;
    public int PacketWindow { get; init; } = 1024;
    public int MaximumSentHistory { get; init; } = 1024;
    public int MaximumPacketsPerUpdate { get; init; } = 16;
    public int MaximumSendAttempts { get; init; } = 8;
    public long RetransmitMilliseconds { get; init; } = 250;
    public long MaximumRetransmitMilliseconds { get; init; } = 2000;
    public long IdleTimeoutMilliseconds { get; init; } = 30000;

    internal void Validate()
    {
        if (MaximumChannels is < 1 or > 1023 || MaximumPendingBunches is < 1 or > 256
            || MaximumBufferedBunches is < 1 or > 256 || MaximumBufferedBytes is < 1 or > 131072
            || ReorderWindow is < 1 or > 255 || PacketWindow is < 1 or > 4096
            || MaximumSentHistory is < 1 or > 4096 || MaximumPacketsPerUpdate is < 1 or > 64
            || MaximumSendAttempts is < 1 or > 32 || RetransmitMilliseconds < 1
            || MaximumRetransmitMilliseconds < RetransmitMilliseconds || MaximumRetransmitMilliseconds > 60000
            || IdleTimeoutMilliseconds < 1 || IdleTimeoutMilliseconds > 3600000)
            throw new ArgumentOutOfRangeException(nameof(Ue3ReliabilityOptions));
    }
}

public enum Ue3ChannelStart { AwaitRemoteOpen, OpenAlreadyVerified }

public sealed record Ue3PeerUpdate(IReadOnlyList<byte[]> Packets, IReadOnlyList<Ue3Bunch> Delivered,
    IReadOnlyList<long> AcknowledgedReliableIds, bool IsDisconnected, string? DisconnectReason);

/// <summary>
/// Bounded single-peer, single-thread reliable transport using plaintext UE3 packets. Callers own AES/UDP
/// and game-control state. Ordinal recovery/reordering follows native 0x0123A150/0x011ED620, referenced in
/// docs/research/native-protocol-evidence.md. Delivers opaque bunches only to explicitly registered
/// channels; creates no players, assumes no handshake success, and opens no Actor channels automatically.
/// </summary>
public sealed class Ue3ReliablePeer
{
    private sealed class Channel(int type, bool open, long incoming, long outgoing)
    {
        public int Type = type;
        public bool Open = open;
        public bool LocalClosing;
        public bool RemoteClosed;
        public long NextIncoming = incoming;
        public long NextOutgoing = outgoing;
        public long LastUnreliablePacket = -1;
        public SortedDictionary<long, Ue3Bunch> Buffered = [];
    }

    private sealed class Pending(long id, long sequence, Ue3Bunch bunch)
    {
        public long Id = id;
        public long Sequence = sequence;
        public Ue3Bunch Bunch = bunch;
        public int Attempts;
        public long LastSent;
        public long LastPacket = -1;
        public bool FastRetry;
    }

    private readonly Ue3ReliabilityOptions options;
    private Dictionary<int, Channel> channels = [];
    private SortedDictionary<long, Pending> pending = [];
    private SortedDictionary<long, Pending> sent = [];
    private HashSet<long> receivedPackets = [];
    private HashSet<int> pendingAcks = [];
    private long? highestReceivedPacket;
    private long highestAck;
    private long nextPacket;
    private long nextReliableId = 1;
    private long lastNow;
    private long lastReceive;
    private int pendingBytes;
    private int bufferedBytes;
    private int bufferedBunches;
    private Ue3PeerUpdate? lastReceiveUpdate;
    private Ue3Bunch[] lastReceiveDelivered = [];
    private bool validatingHandoff;

    public bool IsDisconnected { get; private set; }
    public string? DisconnectReason { get; private set; }
    public int PendingReliableCount => pending.Count;
    public int BufferedReliableCount => bufferedBunches;
    public int RetainedPacketCount => sent.Count;

    /// <summary>
    /// Enqueue budget for shared scheduling. Even unsent pending entries reserve a send-history entry; do
    /// not count only transmitted aliases.
    /// </summary>
    internal int AvailableReliableSlots => IsDisconnected ? 0 : Math.Max(0,
        Math.Min(options.MaximumPendingBunches - pending.Count,
            options.MaximumSentHistory - sent.Count - pending.Values.Count(item => item.Attempts == 0)));
    internal int AvailableReliableBytes => IsDisconnected ? 0 : Math.Max(0, options.MaximumBufferedBytes - pendingBytes);

    /// <summary>
    /// In-flight handshake messages also occupy the control sequence window; consider more than new-stage
    /// batches.
    /// </summary>
    internal int AvailableReliableSlotsForChannel(int channelIndex)
    {
        if (IsDisconnected || !channels.TryGetValue(channelIndex, out var channel)) return 0;

        var oldest = pending.Values.FirstOrDefault(item => item.Bunch.ChannelIndex == channelIndex);
        long windowSlots = oldest is null ? options.ReorderWindow + 1L
            : options.ReorderWindow + 1L - (channel.NextOutgoing - oldest.Sequence);

        return (int)Math.Max(0, Math.Min(AvailableReliableSlots, windowSlots));
    }

    internal Ue3ReliabilityOptions ReliabilityOptions => options;

    /// <summary>
    /// Advances the existing clock/idle deadline on handoff consumption without generating ACK or
    /// retransmission output.
    /// </summary>
    internal void AdvanceHandoffClock(long nowMilliseconds)
    {
        if (!Advance(nowMilliseconds)) throw new InvalidOperationException("交接的可靠连接已超时或断开。");
    }

    public Ue3ReliablePeer(Ue3ReliabilityOptions? options = null, long nowMilliseconds = 0, int initialPacketId = 0)
    {
        this.options = options ?? new();
        this.options.Validate();
        if (nowMilliseconds < 0 || initialPacketId is < 0 or >= Ue3PacketCodec.PacketIdMaximum)
            throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        lastNow = lastReceive = nowMilliseconds;
        nextPacket = initialPacketId;
        highestAck = initialPacketId - 1L;
    }

    /// <summary>
    /// The caller explicitly authorizes channels/types. Default reliable start 1 comes from actual first
    /// Hello/Challenge. Other starts support trusted recovery/testing only, never peer-first-packet
    /// negotiation. Closed channels cannot be reused.
    /// </summary>
    public void RegisterChannel(int index, int type, Ue3ChannelStart start, int nextIncomingSequence = 1, int nextOutgoingSequence = 1)
    {
        RequireConnected();
        if (index is < 0 or >= Ue3PacketCodec.ChannelMaximum || type is < 1 or >= Ue3PacketCodec.ChannelTypeMaximum
            || nextIncomingSequence is < 0 or >= 1024 || nextOutgoingSequence is < 0 or >= 1024
            || !Enum.IsDefined(start)) throw new ArgumentOutOfRangeException(nameof(index));
        if (channels.ContainsKey(index)) throw new InvalidOperationException("通道已注册；不支持在同一连接中复用通道编号。");
        if (channels.Count == options.MaximumChannels) { Disconnect("channel-limit"); return; }
        channels.Add(index, new Channel(type, start == Ue3ChannelStart.OpenAlreadyVerified, nextIncomingSequence, nextOutgoingSequence));
    }

    /// <summary>
    /// Returns a local delivery ID. Its ACK proves peer receipt only, not game-business success.
    /// </summary>
    public long QueueReliable(int channelIndex, ReadOnlySpan<byte> payload, int payloadBitLength, bool open = false, bool close = false)
    {
        RequireConnected();
        if (!channels.TryGetValue(channelIndex, out Channel? channel)) throw new InvalidOperationException("通道尚未注册。");
        if (channel.LocalClosing || channel.RemoteClosed || (!channel.Open && !open))
            throw new InvalidOperationException("通道未打开或已关闭。");
        if (open && channel.Open) throw new InvalidOperationException("不能重复发起通道打开。");
        if (payload.Length > 512) throw new ArgumentOutOfRangeException(nameof(payload));

        var bunch = new Ue3Bunch(open || close, open, close, true, channelIndex,
            (int)(channel.NextOutgoing & 1023), channel.Type, payloadBitLength, payload.ToArray());

        // Validate headers, bit counts, and packet limits before reserving state. No fragmentation; explicitly reject oversized individual bunches.
        Ue3PacketCodec.Encode(new Ue3Packet(0, [bunch]));

        // Pending-count limits alone are insufficient: an old lost packet with newer ACKed packets can still exceed the ten-bit half-window.
        // Conservatively bound send span by the local reorder window. This capacity policy is not a recovered native constant.
        Pending? oldest = pending.Values.FirstOrDefault(item => item.Bunch.ChannelIndex == channelIndex);

        if (oldest is not null && channel.NextOutgoing - oldest.Sequence > options.ReorderWindow)
        {
            Disconnect("outgoing-channel-window");
            return 0;
        }
        if (pending.Count == options.MaximumPendingBunches || payload.Length > options.MaximumBufferedBytes - pendingBytes)
        {
            Disconnect("send-buffer-limit");
            return 0;
        }

        long id = nextReliableId++;

        pending.Add(id, new Pending(id, channel.NextOutgoing, bunch));
        pendingBytes += payload.Length;
        channel.NextOutgoing++;
        channel.Open |= open;
        channel.LocalClosing |= close;
        return id;
    }

    public Ue3PeerUpdate Receive(ReadOnlySpan<byte> plaintext, long nowMilliseconds)
    {
        var update = ReceiveCore(plaintext, nowMilliseconds, flush: true);

        // Callers can mutate delivered lists/payloads; handoff must recheck the complete bytes actually delivered at that time.
        lastReceiveDelivered = update.Delivered.Select(bunch => bunch with { Payload = (byte[])bunch.Payload.Clone() }).ToArray();
        lastReceiveUpdate = update;
        return update;
    }

    /// <summary>
    /// Transfers complete reliable state and permanently disables this instance after a trusted handshake
    /// handler approves every control message in the final batch. The validator checks stage
    /// semantics/authentication; this method establishes complete syntax and exclusive handoff only.
    /// Validation failure retains the old owner. Reject mutation reentry during validation; callers must
    /// still send previous Receive output.
    /// </summary>
    /// <param name="finalReceive">
    /// The original result of this instance's latest Receive, never Tick, an old result, or a fabricated
    /// result.
    /// </param>
    /// <param name="validateMessage">
    /// Side-effect-free approval of each final-batch message to finish the current handshake stage.
    /// </param>
    /// <returns>A one-use state token, which must be disposed if continuation is abandoned.</returns>
    public Ue3ReliableControlHandoff TransferControlChannel(Ue3PeerUpdate finalReceive,
        Func<Ue3ControlMessage, bool> validateMessage)
    {
        RequireConnected();
        ArgumentNullException.ThrowIfNull(finalReceive);
        ArgumentNullException.ThrowIfNull(validateMessage);
        if (!ReferenceEquals(lastReceiveUpdate, finalReceive))
            throw new InvalidOperationException("交接必须验证本 peer 最后一次接收的完整交付批次。");
        if (channels.Count != 1 || !channels.TryGetValue(0, out var channel)
            || channel.Type != 1 || !channel.Open || channel.LocalClosing || channel.RemoteClosed)
            throw new InvalidOperationException("只能交接唯一已打开且未关闭的控制通道。");

        // Preflight the whole batch's syntax before trusted stage validation. Never skip same-bunch or packet tails after Login.
        var messages = new List<Ue3ControlMessage>();

        foreach (var bunch in lastReceiveDelivered)
        {
            if (bunch.ChannelIndex != 0 || bunch.ChannelType != 1 || !bunch.Reliable || bunch.Close
                || bunch.PayloadBitLength == 0 || (bunch.PayloadBitLength & 7) != 0)
                throw new InvalidDataException("交接批次必须是非空整字节可靠控制消息。");

            var stream = Ue3ControlCodec.DecodeKnownStream(bunch.Payload);

            if (stream.Status != Ue3ControlDecodeStatus.Complete)
                throw new InvalidDataException("交接批次包含未完整识别的控制消息。");
            messages.AddRange(stream.Messages);
        }

        validatingHandoff = true;
        try
        {
            foreach (var message in messages)
                if (!validateMessage(message)) throw new InvalidDataException("握手处理器拒绝交接批次中的控制消息。");
        }
        finally { validatingHandoff = false; }

        var destination = new Ue3ReliablePeer(options, lastNow)
        {
            channels = channels, pending = pending, sent = sent, receivedPackets = receivedPackets, pendingAcks = pendingAcks,
            highestReceivedPacket = highestReceivedPacket, highestAck = highestAck, nextPacket = nextPacket,
            nextReliableId = nextReliableId, lastReceive = lastReceive,
            pendingBytes = pendingBytes, bufferedBytes = bufferedBytes, bufferedBunches = bufferedBunches
        };
        var handoff = new Ue3ReliableControlHandoff(destination,
            (int)(channel.NextIncoming & 1023), (int)(channel.NextOutgoing & 1023));

        // Move containers instead of copying ordinals. Old references retain empty containers, so Disconnect cannot clear the new owner's in-flight state.
        channels = []; pending = []; sent = []; receivedPackets = []; pendingAcks = [];
        pendingBytes = bufferedBytes = bufferedBunches = 0;
        lastReceiveUpdate = null;
        lastReceiveDelivered = [];
        IsDisconnected = true;
        DisconnectReason = "control-state-transferred";
        return handoff;
    }

    /// <summary>
    /// Performs transport reception only. The shared owner validates the full business batch across all
    /// channels before a unified Tick.
    /// </summary>
    internal Ue3PeerUpdate ReceiveDeferred(ReadOnlySpan<byte> plaintext, long nowMilliseconds)
        => ReceiveCore(plaintext, nowMilliseconds, flush: false);

    private Ue3PeerUpdate ReceiveCore(ReadOnlySpan<byte> plaintext, long nowMilliseconds, bool flush)
    {
        var delivered = new List<Ue3Bunch>();
        var acknowledged = new List<long>();

        if (!Advance(nowMilliseconds)) return Result([], delivered, acknowledged);

        Ue3Packet packet;

        try { packet = Ue3PacketCodec.Decode(plaintext); }
        catch (InvalidDataException) { Disconnect("invalid-packet"); return Result([], delivered, acknowledged); }

        long packetId;

        try { packetId = highestReceivedPacket is long highest ? Expand(packet.PacketId, highest, 16384) : packet.PacketId; }
        catch (InvalidDataException) { Disconnect("ambiguous-packet-sequence"); return Result([], delivered, acknowledged); }
        if (highestReceivedPacket is long prior && packetId <= prior - options.PacketWindow)
            return Result([], delivered, acknowledged); // Overly old traffic neither delivers nor refreshes idle time.
        lastReceive = nowMilliseconds;
        if (packet.Records.Any(record => record is Ue3Bunch)) pendingAcks.Add(packet.PacketId);
        if (!receivedPackets.Add(packetId)) return Result(flush ? Flush(nowMilliseconds) : [], delivered, acknowledged);
        highestReceivedPacket = Math.Max(highestReceivedPacket ?? packetId, packetId);
        receivedPackets.RemoveWhere(id => id <= highestReceivedPacket.Value - options.PacketWindow);

        foreach (Ue3Record record in packet.Records)
        {
            if (record is Ue3Ack ack) ReceiveAck(ack.PacketId, acknowledged);
            else if (record is Ue3Bunch bunch) ReceiveBunch(packetId, bunch, delivered);
            if (IsDisconnected) return Result([], delivered, acknowledged);
        }
        return Result(flush ? Flush(nowMilliseconds) : [], delivered, acknowledged);
    }

    public Ue3PeerUpdate Tick(long nowMilliseconds)
        => Advance(nowMilliseconds) ? Result(Flush(nowMilliseconds), [], []) : Result([], [], []);

    /// <summary>
    /// Disconnects locally and clears caches without fabricating NMT_Failure or guessing remote-close
    /// handshakes.
    /// </summary>
    public void Disconnect(string reason = "local-close")
    {
        RequireNotValidatingHandoff();
        if (IsDisconnected) return;
        IsDisconnected = true;
        DisconnectReason = reason;
        pending.Clear(); sent.Clear(); receivedPackets.Clear(); pendingAcks.Clear(); channels.Clear();
        pendingBytes = bufferedBytes = bufferedBunches = 0;
        lastReceiveUpdate = null;
        lastReceiveDelivered = [];
    }

    private void ReceiveAck(int wireId, List<long> acknowledged)
    {
        if (nextPacket == 0) return;

        long packetId;

        try { packetId = Expand(wireId, nextPacket - 1, 16384); }
        catch (InvalidDataException) { Disconnect("ambiguous-ack-sequence"); return; }
        if (packetId < 0 || packetId >= nextPacket) return; // ACKs for never-sent packets confirm/cancel no work.
        if (sent.TryGetValue(packetId, out Pending? acknowledgedItem))
        {
            acknowledged.Add(acknowledgedItem.Id);
            RemovePending(acknowledgedItem);
        }
        if (packetId <= highestAck) return;
        // Native ACK gaps trigger NAK. Retransmit only bunches whose latest transmission still lies in the gap, avoiding repeated-ACK amplification.
        foreach (Pending item in pending.Values)
            if (item.LastPacket > highestAck && item.LastPacket < packetId) item.FastRetry = true;
        highestAck = packetId;
    }

    private void ReceiveBunch(long packetId, Ue3Bunch bunch, List<Ue3Bunch> delivered)
    {
        if (!channels.TryGetValue(bunch.ChannelIndex, out Channel? channel)) { Disconnect("unregistered-channel"); return; }
        if ((bunch.Reliable || bunch.Open) && bunch.ChannelType != channel.Type) { Disconnect("channel-type-mismatch"); return; }
        if (!bunch.Reliable)
        {
            if (bunch.Open || bunch.Close) { Disconnect("unsupported-unreliable-channel-control"); return; }
            if (!channel.Open || channel.RemoteClosed) { Disconnect("unreliable-before-open"); return; }
            if (packetId >= channel.LastUnreliablePacket)
            {
                channel.LastUnreliablePacket = packetId;
                delivered.Add(bunch);
            }
            return;
        }

        long sequence;

        try { sequence = Expand(bunch.ChannelSequence, channel.NextIncoming - 1, 1024); }
        catch (InvalidDataException) { Disconnect("ambiguous-channel-sequence"); return; }
        if (sequence < channel.NextIncoming) return;
        if (channel.RemoteClosed) { Disconnect("data-after-channel-close"); return; }
        if (sequence - channel.NextIncoming > options.ReorderWindow) { Disconnect("channel-reorder-limit"); return; }
        if (sequence == channel.NextIncoming)
        {
            Deliver(channel, bunch, delivered);
            while (!IsDisconnected && channel.Buffered.Remove(channel.NextIncoming, out Ue3Bunch? waiting))
            {
                bufferedBytes -= waiting.Payload.Length;
                bufferedBunches--;
                Deliver(channel, waiting, delivered);
            }
            return;
        }
        if (channel.Buffered.TryGetValue(sequence, out Ue3Bunch? existing))
        {
            if (!SameBunch(existing, bunch)) Disconnect("conflicting-reliable-duplicate");
            return;
        }
        if (bufferedBunches == options.MaximumBufferedBunches || bunch.Payload.Length > options.MaximumBufferedBytes - bufferedBytes)
        {
            Disconnect("receive-buffer-limit"); return;
        }
        channel.Buffered.Add(sequence, bunch);
        bufferedBunches++;
        bufferedBytes += bunch.Payload.Length;
    }

    private void Deliver(Channel channel, Ue3Bunch bunch, List<Ue3Bunch> delivered)
    {
        if (channel.RemoteClosed || (!channel.Open && !bunch.Open)) { Disconnect("reliable-before-open"); return; }
        if (bunch.Open && channel.Open) { Disconnect("unsupported-channel-reopen"); return; }
        channel.Open |= bunch.Open;
        channel.NextIncoming++;
        delivered.Add(bunch);
        if (!bunch.Close) return;
        channel.RemoteClosed = true;
        channel.Open = false;
        foreach (Pending item in pending.Values.Where(item => item.Bunch.ChannelIndex == bunch.ChannelIndex).ToArray()) RemovePending(item);
        if (channel.Buffered.Count > 0) Disconnect("buffered-data-after-close");
    }

    private List<byte[]> Flush(long now)
    {
        var packets = new List<byte[]>();

        if (IsDisconnected) return packets;
        if (sent.Count > 0 && nextPacket - sent.First().Key >= 8192) { Disconnect("outgoing-sequence-window"); return packets; }
        if (pendingAcks.Count > 0)
        {
            packets.Add(Ue3PacketCodec.Encode(new Ue3Packet(NextWirePacketId(), pendingAcks.Order().Select(id => (Ue3Record)new Ue3Ack(id)).ToArray())));
            pendingAcks.Clear();
        }
        foreach (Pending item in pending.Values)
        {
            if (packets.Count == options.MaximumPacketsPerUpdate) break;

            long delay = options.RetransmitMilliseconds;

            for (int attempt = 1; attempt < item.Attempts && delay < options.MaximumRetransmitMilliseconds; attempt++)
                delay = Math.Min(options.MaximumRetransmitMilliseconds, delay * 2);
            if (item.Attempts > 0 && !item.FastRetry && now - item.LastSent < delay) continue;
            if (item.Attempts == options.MaximumSendAttempts) { Disconnect("retransmit-limit"); return []; }
            if (sent.Count == options.MaximumSentHistory) { Disconnect("sent-history-limit"); return []; }
            if (sent.Count > 0 && nextPacket - sent.First().Key >= 8192) { Disconnect("outgoing-sequence-window"); return []; }

            long serial = nextPacket;

            packets.Add(Ue3PacketCodec.Encode(new Ue3Packet(NextWirePacketId(), [item.Bunch])));
            sent.Add(serial, item);
            item.LastSent = now;
            item.LastPacket = serial;
            item.Attempts++;
            item.FastRetry = false;
        }
        return packets;
    }

    private int NextWirePacketId() => (int)(nextPacket++ & 16383);

    private void RemovePending(Pending item)
    {
        if (!pending.Remove(item.Id)) return;
        pendingBytes -= item.Bunch.Payload.Length;
        foreach (long packet in sent.Where(pair => ReferenceEquals(pair.Value, item)).Select(pair => pair.Key).ToArray()) sent.Remove(packet);
    }

    private bool Advance(long now)
    {
        RequireNotValidatingHandoff();
        if (now < lastNow) throw new ArgumentOutOfRangeException(nameof(now), "必须传入单调递增的毫秒时钟。");
        lastNow = now;
        if (!IsDisconnected && now - lastReceive >= options.IdleTimeoutMilliseconds) Disconnect("idle-timeout");
        return !IsDisconnected;
    }

    private Ue3PeerUpdate Result(IReadOnlyList<byte[]> packets, IReadOnlyList<Ue3Bunch> delivered, IReadOnlyList<long> acknowledged)
        => IsDisconnected ? new([], [], [], true, DisconnectReason) : new(packets, delivered, acknowledged, false, null);

    private void RequireConnected()
    {
        RequireNotValidatingHandoff();
        if (IsDisconnected) throw new InvalidOperationException("对端传输已断开。");
    }

    private void RequireNotValidatingHandoff()
    {
        if (validatingHandoff) throw new InvalidOperationException("交接批次校验期间不能重入修改可靠状态。");
    }

    // Native formula: ((wire-reference-half) & (modulus-1)) + reference-half.
    // Exact half-window ordering is ambiguous. Reject it explicitly rather than treating uncertain ordinals as new messages.
    private static long Expand(int wire, long reference, int modulus)
    {
        int delta = (wire - (int)(reference & (modulus - 1)) + modulus) & (modulus - 1);

        if (delta == modulus / 2) throw new InvalidDataException("序号恰好落在歧义半窗口。");
        if (delta > modulus / 2) delta -= modulus;
        return reference + delta;
    }

    private static bool SameBunch(Ue3Bunch left, Ue3Bunch right)
        => left.Control == right.Control && left.Open == right.Open && left.Close == right.Close
        && left.ChannelType == right.ChannelType && left.PayloadBitLength == right.PayloadBitLength
        && left.Payload.AsSpan().SequenceEqual(right.Payload);
}
