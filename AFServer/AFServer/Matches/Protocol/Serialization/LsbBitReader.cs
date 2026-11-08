namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// AF UE3 outer fields read each byte least-significant bit first. Effective-bit bounds exclude packet
/// terminators/final padding. Existing samples establish bit order; adaptive SerializeInt termination
/// follows FBitReader::SerializeInt at 0x0046E2A0.
/// </summary>
public ref struct LsbBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private readonly int bitLength;
    public int Position { get; private set; }
    public readonly int Remaining => bitLength - Position;

    public LsbBitReader(ReadOnlySpan<byte> data, int bitLength)
        : this(data, bitLength, NativeDsDatagramCodec.MaximumPlaintextBytes)
    {
    }

    /// <summary>
    /// Explicit bounded read budget for transparent AFDEV outer packets. Legacy callers retain the 512-byte
    /// limit.
    /// </summary>
    public LsbBitReader(ReadOnlySpan<byte> data, int bitLength, int maximumBytes)
    {
        if (maximumBytes is < 0 or > 65503)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (data.Length > maximumBytes || bitLength < 0 || bitLength > data.Length * 8)
            throw new ArgumentOutOfRangeException(nameof(bitLength));
        this.data = data;
        this.bitLength = bitLength;
        Position = 0;
    }

    public bool ReadBit() => ReadBits(1) != 0;

    public uint ReadBits(int count)
    {
        if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        Require(count);

        uint value = 0;

        for (int index = 0; index < count; index++)
        {
            int bit = Position + index;

            value |= (uint)((data[bit >> 3] >> (bit & 7)) & 1) << index;
        }
        Position += count;
        return value;
    }

    /// <summary>
    /// Maximum is exclusive. Native 0x0046E41B–0x0046E421 continues while the read low-bit value plus the
    /// next mask is below it. Power-of-two bounds have fixed width; other bounds may stop early by prefix.
    /// Truncation preserves position.
    /// </summary>
    public int ReadSerializedInt(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));

        var cursor = this;
        uint value = 0;

        for (uint mask = 1; value + mask < (uint)exclusiveMaximum; mask <<= 1)
            if (cursor.ReadBit()) value |= mask;
        this = cursor;
        return (int)value;
    }

    public byte[] ReadPayload(int count)
    {
        Require(count);

        byte[] result = new byte[(count + 7) / 8];

        for (int index = 0; index < count; index++)
            if (ReadBit()) result[index >> 3] |= (byte)(1 << (index & 7));
        return result;
    }

    private readonly void Require(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException("UE3 位流已截断。");
    }

}
