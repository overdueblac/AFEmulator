using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Captured UE3 FString: little-endian signed length, positive for ANSI bytes and negative for UTF-16LE
/// code units, including terminators. Sources: assaultfire_server_v143b.py:9548–9553,9580–9612. Bound each
/// control payload; never allocate large buffers from peer lengths.
/// </summary>
public static class FStringCodec
{
    public const int MaximumEncodedBytes = NativeDsDatagramCodec.MaximumPlaintextBytes;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    public static string Read(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset < 0 || offset > data.Length || data.Length - offset < 4)
            throw new InvalidDataException("FString 长度字段已截断。");

        int count = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        int position = offset + 4;

        if (count == 0)
        {
            offset = position;
            return string.Empty;
        }

        long byteCount = count > 0 ? count : -(long)count * 2;

        if (byteCount > MaximumEncodedBytes - 4 || byteCount > data.Length - position)
            throw new InvalidDataException("FString 超出控制载荷边界。");

        ReadOnlySpan<byte> raw = data.Slice(position, (int)byteCount);
        string value;

        if (count > 0)
        {
            if (raw[^1] != 0) throw new InvalidDataException("ANSI FString 缺少终止符。");
            value = Encoding.Latin1.GetString(raw[..^1]);
        }
        else
        {
            if (raw[^1] != 0 || raw[^2] != 0) throw new InvalidDataException("UTF-16 FString 缺少终止符。");
            try { value = StrictUnicode.GetString(raw[..^2]); }
            catch (DecoderFallbackException exception) { throw new InvalidDataException("UTF-16 FString 编码无效。", exception); }
        }
        offset = position + (int)byteCount;
        return value;
    }

    public static byte[] Write(string value, bool unicode = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return new byte[4];

        int width = unicode ? 2 : 1;

        if (value.Length > (MaximumEncodedBytes - 4) / width - 1)
            throw new ArgumentOutOfRangeException(nameof(value), "FString 超出控制载荷上限。");
        if (!unicode && value.Any(character => character > 0x7F))
            throw new ArgumentException("已验证的 ANSI 写入路径只发送 ASCII；其他字符必须选择 UTF-16LE。", nameof(value));

        byte[] result = new byte[4 + (value.Length + 1) * width];

        BinaryPrimitives.WriteInt32LittleEndian(result, unicode ? -(value.Length + 1) : value.Length + 1);
        try
        {
            if (unicode) StrictUnicode.GetBytes(value.AsSpan(), result.AsSpan(4));
            else Encoding.ASCII.GetBytes(value.AsSpan(), result.AsSpan(4));
        }
        catch (EncoderFallbackException exception) { throw new ArgumentException("FString 包含无效 UTF-16。", nameof(value), exception); }
        return result;
    }
}
