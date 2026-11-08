using System.Security.Cryptography;

namespace AFServer.AssaultFire.Protocol;

/// <summary>
/// Historical encryption envelope for the 2014 client. Fixed IVs, padding, and hashes preserve existing
/// wire compatibility only.
/// </summary>
public static class LegacyCrypto
{
    public static readonly byte[] ApIv = Convert.FromHexString("b83bca76c4fd6d3ba5964edeb300df6c");
    private static readonly byte[] Mode3Iv = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();

    public static byte[] ApEncrypt(byte[] key, byte[] plain)
    {
        if (plain.Length == 0) throw new InvalidDataException("AP 明文不能为空。");

        int tail = (plain.Length - 1) % 16 + 1;
        byte[] padded = new byte[plain.Length - tail + 32];

        plain.CopyTo(padded, 0);
        padded[^16] = (byte)(16 - tail);

        using var aes = Aes.Create();

        aes.Key = key;
        return aes.EncryptCbc(padded, ApIv, PaddingMode.None);
    }

    public static byte[] ApDecrypt(byte[] key, byte[] cipher)
    {
        if (cipher.Length < 32 || cipher.Length % 16 != 0) throw new InvalidDataException("AP 密文长度无效。");

        using var aes = Aes.Create();

        aes.Key = key;

        byte[] raw = aes.DecryptCbc(cipher, ApIv, PaddingMode.None);

        if (raw[^16] > 15) throw new InvalidDataException("AP 填充无效。");
        return raw[..(raw.Length - 16 - raw[^16])];
    }

    public static byte[] Encrypt(byte[] plain, byte[] key, int mode, bool deterministic = false)
    {
        if (key.Length != 16) throw new InvalidDataException("传输密钥必须为 16 字节。");

        using var aes = Aes.Create();

        aes.Key = key;
        if (mode == 3)
        {
            int remainder = plain.Length % 16;
            int padding = (remainder <= 10 ? 16 : 32) - remainder;
            byte[] framed = new byte[plain.Length + padding];

            plain.CopyTo(framed, 0);
            if (!deterministic) RandomNumberGenerator.Fill(framed.AsSpan(plain.Length, padding - 6));
            "tsf4g"u8.CopyTo(framed.AsSpan(framed.Length - 6));
            framed[^1] = (byte)padding;
            return aes.EncryptCbc(framed, Mode3Iv, PaddingMode.None);
        }
        if (mode != 4) throw new NotSupportedException($"不支持传输加密模式 {mode}。");

        int pad = (16 - ((plain.Length + 10) & 15)) & 15;
        byte[] input = new byte[plain.Length + pad + 10];

        if (!deterministic) RandomNumberGenerator.Fill(input.AsSpan(0, pad + 3));
        input[0] = (byte)((input[0] & 0xf0) | pad);
        plain.CopyTo(input, pad + 3);

        byte[] result = new byte[input.Length], previousPlain = new byte[16], previousCipher = new byte[16];

        for (int offset = 0; offset < input.Length; offset += 16)
        {
            byte[] block = input[offset..(offset + 16)];
            byte[] cipher = aes.EncryptEcb(Xor(block, previousCipher), PaddingMode.None);

            cipher = Xor(cipher, previousPlain);
            cipher.CopyTo(result, offset);
            previousPlain = block;
            previousCipher = cipher;
        }
        return result;
    }

    public static byte[] Decrypt(byte[] cipher, byte[] key, int mode)
    {
        if (key.Length != 16 || cipher.Length == 0 || cipher.Length % 16 != 0) throw new InvalidDataException("传输密文长度无效。");

        using var aes = Aes.Create();

        aes.Key = key;
        if (mode == 3)
        {
            byte[] raw = aes.DecryptCbc(cipher, Mode3Iv, PaddingMode.None);
            int padding = raw[^1];

            if (!raw.AsSpan(raw.Length - 6, 5).SequenceEqual("tsf4g"u8) || padding < 6 || padding > raw.Length)
                throw new InvalidDataException("mode3 填充无效。");

            int remainder = (raw.Length - padding) % 16;

            if (padding != (remainder <= 10 ? 16 : 32) - remainder) throw new InvalidDataException("mode3 填充长度不匹配。");
            return raw[..^padding];
        }
        if (mode != 4) throw new NotSupportedException($"不支持传输加密模式 {mode}。");

        byte[] result = new byte[cipher.Length], previousPlain = new byte[16], previousCipher = new byte[16];

        for (int offset = 0; offset < cipher.Length; offset += 16)
        {
            byte[] block = cipher[offset..(offset + 16)];
            byte[] plain = Xor(aes.DecryptEcb(Xor(block, previousPlain), PaddingMode.None), previousCipher);

            plain.CopyTo(result, offset);
            previousPlain = plain;
            previousCipher = block;
        }

        int start = (result[0] & 15) + 3;

        if (start > result.Length - 7 || result.AsSpan(result.Length - 7).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("mode4 尾部校验失败。");
        return result[start..^7];
    }

    private static byte[] Xor(byte[] left, byte[] right)
    {
        byte[] result = new byte[left.Length];

        for (int i = 0; i < result.Length; i++) result[i] = (byte)(left[i] ^ right[i]);
        return result;
    }
}
