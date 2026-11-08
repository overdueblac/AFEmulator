namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Recovered AF UProperty primitive values, excluding field IDs, array indices, and Actor state. Actor
/// receiver 0x011F944F invokes property virtual slot +0x164; fields pack from current bit position without
/// byte alignment. Truncation/capacity failure throws InvalidDataException and consumes no field bits.
/// </summary>
public static class Ue3PrimitivePropertyCodec
{
    /// <summary>UBoolProperty 0x00502380 sends one bit through Archive::SerializeBits.</summary>
    public static bool ReadBool(ref LsbBitReader reader) => reader.ReadBit();

    /// <summary>Writes one Bool bit, not the host storage byte or bit mask.</summary>
    public static void WriteBool(LsbBitWriter writer, bool value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBit(value);
    }

    /// <summary>
    /// UIntProperty's virtual slot points to 0x00503C50. Serialize(...,4) transmits a little-endian signed
    /// 32-bit value.
    /// </summary>
    public static int ReadInt32(ref LsbBitReader reader) => unchecked((int)reader.ReadBits(32));

    /// <summary>
    /// Writes Int32 two's-complement bits least-significant first, without requiring byte alignment.
    /// </summary>
    public static void WriteInt32(LsbBitWriter writer, int value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBits(unchecked((uint)value), 32);
    }

    /// <summary>
    /// UFloatProperty also uses 0x00503C50 four-byte serialization. Returns raw IEEE 754 bits without
    /// arithmetic/conversion, preserving signed zero, infinity, and all NaN payloads including signaling
    /// NaNs.
    /// </summary>
    public static uint ReadFloat32Bits(ref LsbBitReader reader) => reader.ReadBits(32);

    /// <summary>Writes raw IEEE 754 binary32 bits without NaN normalization or value quantization.</summary>
    public static void WriteFloat32Bits(LsbBitWriter writer, uint bits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBits(bits, 32);
    }

    /// <summary>
    /// Only for UByteProperty with null Enum. This 0x00505400 branch calls SerializeBits(...,8).
    /// Enum-backed bytes have separate width rules and must not use this method.
    /// </summary>
    public static byte ReadByte(ref LsbBitReader reader) => (byte)reader.ReadBits(8);

    /// <summary>
    /// Writes all eight bits of a nonenum Byte; the caller must confirm Enum is absent first.
    /// </summary>
    public static void WriteByte(LsbBitWriter writer, byte value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBits(value, 8);
    }
}
