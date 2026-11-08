namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Fixed-capacity least-significant-bit-first writer, rejecting overflow without resizing or truncating
/// packets.
/// </summary>
public sealed class LsbBitWriter
{
    private readonly byte[] data;
    private readonly int capacityBits;
    public int Position { get; private set; }
    public int Remaining => capacityBits - Position;

    public LsbBitWriter(int capacityBits = NativeDsDatagramCodec.MaximumPlaintextBytes * 8)
        : this(capacityBits, NativeDsDatagramCodec.MaximumPlaintextBytes * 8)
    {
    }

    /// <summary>
    /// Allocates a fixed buffer from an explicit transport budget, only for outer reconstruction within a
    /// bounded UDP range.
    /// </summary>
    public LsbBitWriter(int capacityBits, int maximumCapacityBits)
    {
        if (maximumCapacityBits is < 0 or > 65503 * 8)
            throw new ArgumentOutOfRangeException(nameof(maximumCapacityBits));
        if (capacityBits < 0 || capacityBits > maximumCapacityBits)
            throw new ArgumentOutOfRangeException(nameof(capacityBits));
        this.capacityBits = capacityBits;
        data = new byte[(capacityBits + 7) / 8];
    }

    public void WriteBit(bool value) => WriteBits(value ? 1u : 0u, 1);

    public void WriteBits(uint value, int count)
    {
        if (count is < 0 or > 32 || (count < 32 && value >= (1u << count)))
            throw new ArgumentOutOfRangeException(nameof(count), "值不能被指定的位数完整表示。");
        Require(count);
        for (int index = 0; index < count; index++)
        {
            int bit = Position + index;

            if ((value & (1u << index)) != 0) data[bit >> 3] |= (byte)(1 << (bit & 7));
        }
        Position += count;
    }

    /// <summary>
    /// Native FBitWriter::SerializeInt at 0x0046D630 compares the written low-bit prefix plus the next mask
    /// against the exclusive bound. Never use the original full value to control the loop. Calculate bit
    /// count first and commit together, preserving output on insufficient capacity.
    /// </summary>
    public void WriteSerializedInt(int value, int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        if (value < 0 || value >= exclusiveMaximum) throw new ArgumentOutOfRangeException(nameof(value));

        uint prefix = 0;
        int count = 0;

        for (uint mask = 1; prefix + mask < (uint)exclusiveMaximum; mask <<= 1)
        {
            if (((uint)value & mask) != 0) prefix |= mask;
            count++;
        }
        WriteBits((uint)value, count);
    }

    public void WritePayload(ReadOnlySpan<byte> payload, int bitLength)
    {
        if (bitLength < 0 || bitLength > NativeDsDatagramCodec.MaximumPlaintextBytes * 8 || payload.Length != (bitLength + 7) / 8)
            throw new ArgumentException("UE3 载荷字节数与有效位数不一致。", nameof(payload));

        int tailBits = bitLength & 7;

        if (tailBits != 0 && (payload[^1] >> tailBits) != 0)
            throw new ArgumentException("UE3 载荷未使用的高位必须为零。", nameof(payload));
        Require(bitLength);
        for (int index = 0; index < bitLength; index++)
            WriteBit((payload[index >> 3] & (1 << (index & 7))) != 0);
    }

    public byte[] ToArray() => data.AsSpan(0, (Position + 7) / 8).ToArray();

    private void Require(int count)
    {
        if (count > Remaining) throw new InvalidDataException("UE3 输出超过已验证的包长上限。");
    }
}
