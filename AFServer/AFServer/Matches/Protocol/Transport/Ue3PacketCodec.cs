namespace AFServer.AssaultFire.Matches.Protocol;

public abstract record Ue3Record;
public sealed record Ue3Ack(int PacketId) : Ue3Record;

/// <summary>
/// Outer bunch structure only. Control indicates open/close flags, not channel type. Actor payloads stay
/// opaque, with no claim of spawning, property replication, or RPC support. PayloadBitLength is
/// authoritative; remaining high bits in the final byte must be zero.
/// </summary>
public sealed record Ue3Bunch(
    bool Control, bool Open, bool Close, bool Reliable,
    int ChannelIndex, int ChannelSequence, int ChannelType,
    int PayloadBitLength, byte[] Payload) : Ue3Record;

public sealed record Ue3Packet(int PacketId, IReadOnlyList<Ue3Record> Records);

/// <summary>
/// AF UE3 packet headers, ACKs, and bunch boundaries from assaultfire_server_v143b.py:9415–9545. Captures
/// support 14-bit PacketId, ten-bit reliable ordinals, three-bit types, and 12-bit payload lengths. Channel
/// bound is exclusive 1023; adaptive SerializeInt uses nine bits for index 511, ten for other valid
/// indices. Retransmission, deduplication, ordering, and channel lifetimes remain caller-owned.
/// </summary>
public static class Ue3PacketCodec
{
    public const int PacketIdMaximum = 16384;
    public const int ChannelMaximum = 1023;
    public const int ChannelSequenceMaximum = 1024;
    public const int ChannelTypeMaximum = 8;
    public const int PayloadBitLengthMaximum = 4096;
    public const int MaximumRecords = (512 * 8 - 15) / 15;

    public static Ue3Packet Decode(ReadOnlySpan<byte> plaintext)
        => Decode(plaintext, NativeDsDatagramCodec.MaximumPlaintextBytes);

    /// <summary>
    /// Reads outer packets with an explicit bounded transport limit. Transparent AFDEV relays must not
    /// apply the old DS codec's 512-byte policy to all UDP.
    /// </summary>
    public static Ue3Packet Decode(ReadOnlySpan<byte> plaintext, int maximumPlaintextBytes)
    {
        if (maximumPlaintextBytes is < 2 or > 65503)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));
        if (plaintext.Length < 2 || plaintext.Length > maximumPlaintextBytes || plaintext[^1] == 0)
            throw new InvalidDataException("UE3 包长或末字节终止位无效。");

        int lastBit = 7;

        while ((plaintext[^1] & (1 << lastBit)) == 0) lastBit--;

        int stopBit = (plaintext.Length - 1) * 8 + lastBit;

        if (stopBit < 14) throw new InvalidDataException("UE3 包终止位在 PacketId 内部。");

        // Exclude the terminator before creating the reader so truncated headers such as 4000 cannot pass as in old bridge.parse_packet.
        var reader = new LsbBitReader(plaintext, stopBit, maximumPlaintextBytes);
        int packetId = reader.ReadSerializedInt(PacketIdMaximum);
        var records = new List<Ue3Record>();

        while (reader.Remaining > 0)
        {
            if (records.Count == (maximumPlaintextBytes * 8 - 15) / 15) throw new InvalidDataException("UE3 包记录过多。");
            if (reader.ReadBit())
            {
                records.Add(new Ue3Ack(reader.ReadSerializedInt(PacketIdMaximum)));
                continue;
            }

            bool control = reader.ReadBit();
            bool open = control && reader.ReadBit();
            bool close = control && reader.ReadBit();
            bool reliable = reader.ReadBit();
            int channel = reader.ReadSerializedInt(ChannelMaximum);
            int sequence = reliable ? reader.ReadSerializedInt(ChannelSequenceMaximum) : 0;
            int channelType = reliable || open ? reader.ReadSerializedInt(ChannelTypeMaximum) : 0;
            int payloadBits = reader.ReadSerializedInt(PayloadBitLengthMaximum);
            byte[] payload = reader.ReadPayload(payloadBits);

            records.Add(new Ue3Bunch(control, open, close, reliable, channel, sequence, channelType, payloadBits, payload));
        }
        return new Ue3Packet(packetId, records.AsReadOnly());
    }

    public static byte[] Encode(Ue3Packet packet)
        => Encode(packet, NativeDsDatagramCodec.MaximumPlaintextBytes);

    /// <summary>
    /// Rebuilds parsed packets within an explicit transport budget, preserving existing default DS encoding
    /// policy.
    /// </summary>
    public static byte[] Encode(Ue3Packet packet, int maximumPlaintextBytes)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(packet.Records);
        if (maximumPlaintextBytes is < 2 or > 65503)
            throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));
        if (packet.Records.Count > (maximumPlaintextBytes * 8 - 15) / 15)
            throw new ArgumentException("UE3 包记录过多。", nameof(packet));

        var writer = new LsbBitWriter(maximumPlaintextBytes * 8, maximumPlaintextBytes * 8);

        writer.WriteSerializedInt(packet.PacketId, PacketIdMaximum);
        foreach (Ue3Record record in packet.Records)
        {
            switch (record)
            {
                case Ue3Ack ack:
                    writer.WriteBit(true);
                    writer.WriteSerializedInt(ack.PacketId, PacketIdMaximum);
                    break;
                case Ue3Bunch bunch:
                    WriteBunch(writer, bunch);
                    break;
                default:
                    throw new ArgumentException("UE3 记录类型未知。", nameof(packet));
            }
        }
        writer.WriteBit(true);

        byte[] result = writer.ToArray();

        if (result.Length > maximumPlaintextBytes)
            throw new InvalidDataException("UE3 包超出调用方的传输预算。");

        return result;
    }

    public static byte[] EncodeControlPacket(int packetId, int controlSequence, ReadOnlySpan<byte> payload, int? acknowledgedPacketId = null)
    {
        if (payload.Length > NativeDsDatagramCodec.MaximumPlaintextBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var records = new List<Ue3Record>(2);

        if (acknowledgedPacketId is int ack) records.Add(new Ue3Ack(ack));
        records.Add(new Ue3Bunch(false, false, false, true, 0, controlSequence, 1, payload.Length * 8, payload.ToArray()));
        return Encode(new Ue3Packet(packetId, records));
    }

    private static void WriteBunch(LsbBitWriter writer, Ue3Bunch bunch)
    {
        ArgumentNullException.ThrowIfNull(bunch.Payload);
        if ((!bunch.Control && (bunch.Open || bunch.Close)) || (!bunch.Reliable && bunch.ChannelSequence != 0)
            || (!bunch.Reliable && !bunch.Open && bunch.ChannelType != 0))
            throw new ArgumentException("UE3 bunch 含无法编码的条件字段。", nameof(bunch));
        writer.WriteBit(false);
        writer.WriteBit(bunch.Control);
        if (bunch.Control)
        {
            writer.WriteBit(bunch.Open);
            writer.WriteBit(bunch.Close);
        }
        writer.WriteBit(bunch.Reliable);
        writer.WriteSerializedInt(bunch.ChannelIndex, ChannelMaximum);
        if (bunch.Reliable) writer.WriteSerializedInt(bunch.ChannelSequence, ChannelSequenceMaximum);
        if (bunch.Reliable || bunch.Open) writer.WriteSerializedInt(bunch.ChannelType, ChannelTypeMaximum);
        writer.WriteSerializedInt(bunch.PayloadBitLength, PayloadBitLengthMaximum);
        writer.WritePayload(bunch.Payload, bunch.PayloadBitLength);
    }
}
