using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Protocol;

/// <summary>
/// Client F3 configuration container. Server maintenance and CONNECT share decoding, validation, and
/// in-place write rules.
/// </summary>
public static class ClientConfigurationContainer
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly byte[] Magic = [0xF3, 0xF3, 0xF3, 0xF3];
    // Native configuration-format constants, not accounts, tickets, or connection-authentication keys.
    private static readonly byte[] ContainerKey = [0x54, 0x47, 0x41, 0x4D, 0x45, 0x1F, 0xE9, 0x2C,
        0x9A, 0x97, 0x1A, 0x0C, 0xD1, 0xF6, 0x10, 0xFB];

    /// <summary>
    /// Reads bounded configuration without following contained paths or copying client directories.
    /// </summary>
    public static byte[] ReadBounded(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("客户端配置文件过大。");

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Decodes F3 containers/plaintext INI, validating lengths, zlib tails, Adler32, and zero padding.
    /// </summary>
    public static byte[] Decode(byte[] blob)
    {
        if (blob.Length > MaximumBytes) throw new InvalidDataException("客户端配置文件过大。");
        if (!blob.AsSpan().StartsWith(Magic)) return blob.ToArray();
        if (blob.Length <= 4 || (blob.Length - 4) % 16 != 0)
            throw new InvalidDataException("客户端配置密文长度无效。");

        using var aes = Aes.Create();

        aes.Key = ContainerKey;

        var plain = aes.DecryptEcb(blob.AsSpan(4), PaddingMode.None);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(plain);

        if (declared > MaximumBytes) throw new InvalidDataException("客户端配置声明的解压长度过大。");

        // Bound read-ahead and retain compressed-stream end positions to distinguish checksums, padding, and truncation.
        using var compressed = new ExactReadStream(plain[4..]);
        using var stream = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;

        while ((read = stream.Read(buffer)) > 0)
        {
            if (raw.Length + read > MaximumBytes) throw new InvalidDataException("客户端配置解压长度过大。");

            raw.Write(buffer, 0, read);
        }
        if (declared != 0 && declared != raw.Length) throw new InvalidDataException("客户端配置解压长度不符。");

        var result = raw.ToArray();
        var consumed = checked((int)compressed.Position);
        var padding = plain.Length - 4 - consumed;

        if (consumed < 6 || padding is < 0 or > 15 ||
            plain.AsSpan(4 + consumed).ContainsAnyExcept((byte)0) ||
            BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(consumed, 4)) != Adler32(result))
            throw new InvalidDataException("客户端配置压缩流不完整、校验和错误或填充无效。");

        return result;
    }

    /// <summary>Encodes native u32 length, zlib, zero padding, then AES-ECB in that order.</summary>
    public static byte[] Encode(byte[] raw)
    {
        if (raw.Length > MaximumBytes) throw new InvalidDataException("客户端配置明文过大。");

        using var body = new MemoryStream();
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)raw.Length);
        body.Write(length);
        using (var compressed = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true)) compressed.Write(raw);
        while (body.Length % 16 != 0) body.WriteByte(0);

        using var aes = Aes.Create();

        aes.Key = ContainerKey;
        return [.. Magic, .. aes.EncryptEcb(body.ToArray(), PaddingMode.None)];
    }

    /// <summary>
    /// Checks that the original remains unchanged before atomic replacement, retaining a backup and
    /// skipping identical content.
    /// </summary>
    public static string? Replace(string path, byte[] original, byte[] decoded, string backupLabel)
    {
        if (Decode(original).AsSpan().SequenceEqual(decoded)) return null;

        var backup = path + ".bak_" + backupLabel + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture);
        var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");

        try
        {
            File.WriteAllBytes(temporary, Encode(decoded));
            if (!Decode(ReadBounded(temporary)).AsSpan().SequenceEqual(decoded))
                throw new IOException("客户端配置写回校验失败。");
            if (!ReadBounded(path).AsSpan().SequenceEqual(original))
                throw new IOException("客户端配置已被其他程序更新，请重新执行。");

            File.Replace(temporary, path, backup);
            return backup;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class ExactReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }

    private static uint Adler32(byte[] bytes)
    {
        uint first = 1, second = 0;

        foreach (var value in bytes)
        {
            first = (first + value) % 65521;
            second = (second + first) % 65521;
        }
        return second << 16 | first;
    }
}
