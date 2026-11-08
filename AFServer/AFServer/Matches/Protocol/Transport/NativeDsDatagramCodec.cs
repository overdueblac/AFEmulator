using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// AF DS datagram: little-endian u32 plaintext-byte count followed by AES-128-ECB with zero-padded final
/// block. Source: af-emulator/server/assaultfire_server_v143b.py:9314–9357. Selects no default key; callers
/// pass the match key assigned through A11A.
/// </summary>
public static class NativeDsDatagramCodec
{
    public const int KeyBytes = 16;
    public const int MaximumPlaintextBytes = 512;
    public const int MaximumDatagramBytes = 4 + MaximumPlaintextBytes;

    public static byte[] Decrypt(ReadOnlySpan<byte> datagram, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        if (datagram.Length < 20 || datagram.Length > MaximumDatagramBytes || (datagram.Length - 4) % 16 != 0)
            throw new InvalidDataException("DS 数据报长度或 AES 块边界无效。");

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(datagram);

        if (declaredLength is 0 or > MaximumPlaintextBytes)
            throw new InvalidDataException("DS 明文长度超出已验证的 512 字节上限。");

        int length = (int)declaredLength;
        int paddedLength = (length + 15) & ~15;

        if (datagram.Length != 4 + paddedLength)
            throw new InvalidDataException("DS 声明长度与密文块数不一致。");

        Span<byte> padded = stackalloc byte[MaximumPlaintextBytes];
        using Aes aes = CreateCipher(key);

        try
        {
            int written = aes.DecryptEcb(datagram[4..], padded, PaddingMode.None);

            if (written != paddedLength || padded[length..written].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("DS 补齐字节不是已验证的零填充。");
            return padded[..length].ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        ValidateKey(key);
        if (plaintext.IsEmpty || plaintext.Length > MaximumPlaintextBytes)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "DS 明文必须为 1 至 512 字节。");

        int paddedLength = (plaintext.Length + 15) & ~15;
        Span<byte> padded = stackalloc byte[MaximumPlaintextBytes];

        padded.Clear();
        plaintext.CopyTo(padded);

        byte[] datagram = new byte[4 + paddedLength];

        BinaryPrimitives.WriteUInt32LittleEndian(datagram, (uint)plaintext.Length);

        using Aes aes = CreateCipher(key);

        try
        {
            aes.EncryptEcb(padded[..paddedLength], datagram.AsSpan(4), PaddingMode.None);
            return datagram;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
            throw new ArgumentException("DS 使用 16 字节 AES 密钥。", nameof(key));
    }

    private static Aes CreateCipher(ReadOnlySpan<byte> key)
    {
        Aes aes = Aes.Create();
        byte[] copy = key.ToArray();

        try
        {
            aes.Key = copy;
            return aes;
        }
        catch
        {
            aes.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }
}
