using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Protocol;

/// <summary>
/// Centralizes network byte order and length bounds so business handlers do not duplicate packet
/// construction.
/// </summary>
public sealed class Wire
{
    private readonly MemoryStream stream = new();
    public Wire U8(byte value) { stream.WriteByte(value); return this; }
    public Wire U16(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); return this; }
    public Wire U32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); return this; }
    public Wire Bytes(byte[] value) { stream.Write(value); return this; }
    public Wire String(string value) { byte[] bytes = Encoding.ASCII.GetBytes(value + '\0'); return U32((uint)bytes.Length).Bytes(bytes); }
    public byte[] ToArray() => stream.ToArray();
    public static uint Read32(ReadOnlySpan<byte> value, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(value[offset..]);
    public static ushort Read16(ReadOnlySpan<byte> value, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(value[offset..]);
    public static void Write32(Span<byte> target, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);
}
