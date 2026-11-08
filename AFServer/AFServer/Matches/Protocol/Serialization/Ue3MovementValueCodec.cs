namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Vector quantized to integer world units, without implied floating-point rounding, movement simulation,
/// or coordinate validation.
/// </summary>
public readonly record struct Ue3QuantizedVector(int X, int Y, int Z);

/// <summary>
/// Raw UE3 integer angles. Wire format retains component bits 8–15 and wraps at 65,536 units per turn.
/// </summary>
public readonly record struct Ue3Rotator(int Pitch, int Yaw, int Roll);

/// <summary>
/// AF Vector/Rotator property bitstreams. UStructProperty 0x00505D8F dispatches by name: Vector(0x57) to
/// 0x004A8E50, Rotator(0x58) to 0x004A14B0; referenced in docs/research/net-property-evidence.md. Codecs
/// cover values only, not movement, Actor replication, or simulation. Failures preserve reader/writer
/// state.
/// </summary>
public static class Ue3MovementValueCodec
{
    public const int MinimumVectorComponent = -1048576;
    public const int MaximumVectorComponent = 1048575;

    /// <summary>
    /// Reads n-1(Max20), then X/Y/Z with Max=2^(n+1), subtracting 2^n. Returns integer world units.
    /// </summary>
    public static Ue3QuantizedVector ReadQuantizedVector(ref LsbBitReader reader)
    {
        var cursor = reader;
        int width = cursor.ReadSerializedInt(20) + 1;
        int bias = 1 << width;
        int maximum = 1 << (width + 1);
        var value = new Ue3QuantizedVector(cursor.ReadSerializedInt(maximum) - bias,
            cursor.ReadSerializedInt(maximum) - bias, cursor.ReadSerializedInt(maximum) - bias);

        reader = cursor;
        return value;
    }

    /// <summary>
    /// Requires quantized axes within [-1048576,1048575], rejecting overflow without modulo or clamping.
    /// n=clamp(ceil(log2(max(abs(X),abs(Y),abs(Z))+1)),1,20). Native float-to-integer rounding remains
    /// unrecovered and is not handled here.
    /// </summary>
    public static void WriteQuantizedVector(LsbBitWriter writer, Ue3QuantizedVector value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!ValidComponent(value.X) || !ValidComponent(value.Y) || !ValidComponent(value.Z))
            throw new ArgumentOutOfRangeException(nameof(value), "量化Vector各轴必须在-1048576至1048575之间。");

        int magnitude = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        int width = 1;

        while (width < 20 && magnitude >= (1 << width)) width++;

        int header = width - 1;
        // Max20 prefixes 4..15 terminate after four bits; 0..3 and 16..19 require a fifth.
        int headerBits = header is >= 4 and <= 15 ? 4 : 5;

        RequireCapacity(writer, headerBits + 3 * (width + 1));

        int bias = 1 << width;
        int maximum = 1 << (width + 1);

        writer.WriteSerializedInt(header, 20);
        writer.WriteSerializedInt(value.X + bias, maximum);
        writer.WriteSerializedInt(value.Y + bias, maximum);
        writer.WriteSerializedInt(value.Z + bias, maximum);
    }

    /// <summary>
    /// Reads Pitch/Yaw/Roll presence bits and optional eight-bit values, shifts left eight, and returns
    /// 0..65280.
    /// </summary>
    public static Ue3Rotator ReadRotator(ref LsbBitReader reader)
    {
        var cursor = reader;
        int pitch = ReadAngle(ref cursor);
        int yaw = ReadAngle(ref cursor);
        int roll = ReadAngle(ref cursor);

        reader = cursor;
        return new(pitch, yaw, roll);
    }

    /// <summary>
    /// Encodes each component as (component&gt;&gt;8)&amp;255, allowing integer-angle wrap and discarding
    /// low eight bits. Total length is 3..27 bits.
    /// </summary>
    public static void WriteRotator(LsbBitWriter writer, Ue3Rotator value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        byte pitch = (byte)((value.Pitch >> 8) & 255);
        byte yaw = (byte)((value.Yaw >> 8) & 255);
        byte roll = (byte)((value.Roll >> 8) & 255);
        int required = 3 + (pitch == 0 ? 0 : 8) + (yaw == 0 ? 0 : 8) + (roll == 0 ? 0 : 8);

        RequireCapacity(writer, required);
        WriteAngle(writer, pitch);
        WriteAngle(writer, yaw);
        WriteAngle(writer, roll);
    }

    private static bool ValidComponent(int value) => value is >= MinimumVectorComponent and <= MaximumVectorComponent;
    private static int ReadAngle(ref LsbBitReader reader) => reader.ReadBit() ? (int)reader.ReadBits(8) << 8 : 0;

    private static void WriteAngle(LsbBitWriter writer, byte value)
    {
        writer.WriteBit(value != 0);
        if (value != 0) writer.WriteBits(value, 8);
    }

    private static void RequireCapacity(LsbBitWriter writer, int required)
    {
        if (writer.Remaining < required) throw new InvalidDataException("Vector或Rotator超过剩余位流容量。");
    }
}
