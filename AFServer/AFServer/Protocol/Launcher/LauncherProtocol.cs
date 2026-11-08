using System.Numerics;
using System.Security.Cryptography;

namespace AFServer.AssaultFire.Protocol;

/// <summary>Pure VERSION, AUTH, and directory protocols for independent compatibility-vector tests.</summary>
public static class LauncherProtocol
{
    private static readonly BigInteger Prime = new(Convert.FromHexString("BB2A43DF39322DAAC8C3B30C9F21E13F646B7234A846231038C087F3408A558DA8AA1912AE906DEF3E781E39FE172B203A6B8452056B3C9CB21237CAC3F5AA1B"), true, true);

    public static byte[] Version(byte[] request)
    {
        if (request.Length != 65) throw new InvalidDataException("VERSION 请求必须为 65 字节。");

        byte[] response = (byte[])request.Clone();

        Wire.Write32(response, 0, (uint)response.Length);
        response[22] = response[23] = response[26] = response[27] = 0;
        return response;
    }

    public static byte[] Directory(byte[] request, string host = "127.0.0.1", int port = 65005)
    {
        if (request.Length != 26) throw new InvalidDataException("DIR 请求必须为 26 字节。");
        byte[] Category(uint id, uint parent, string name, uint flags) => new Wire().U16(0).U32(id).U32(parent).U32(flags).String(name).U32(0).U32(0).String("").U8(1).U16(0).ToArray();

        byte[] leaf = new Wire().U16(1).U32(0x01010101).U32(0x01010100).U32(3).String("Assault Fire Local")
            .U32(0).String($"tcp://{host}:{port}").String("1.0.0.24").U32(3).U32(0).String("").U8(1)
            // The client's dynamic resolver requires host:port; without a protocol prefix.
            .String($"{host}:{port};").U16(0).ToArray();
        byte[] nodes = new Wire().Bytes(Category(0, 0, "Root", 0)).Bytes(Category(0x01010100, 0, "Assault Fire Local", 3)).Bytes(leaf).ToArray();
        byte[] body = new Wire().U16(3).U8(0x81).U16(3).U16(checked((ushort)nodes.Length)).Bytes(nodes).ToArray();
        byte[] header = (byte[])request.Clone();

        Wire.Write32(header, 4, (uint)body.Length);
        Wire.Write32(header, 12, 0x1772);
        Wire.Write32(header, 20, 0);
        return new Wire().Bytes(header).Bytes(body).ToArray();
    }

    public static (byte[] Response, byte[] Key) Authenticate(byte[] request, RSA rsa)
    {
        if (request.Length != 214) throw new InvalidDataException("AUTH 请求必须为 214 字节。");

        byte[] plain = rsa.Decrypt(request[86..214], RSAEncryptionPadding.Pkcs1);

        if (plain.Length != 68) throw new InvalidDataException("AUTH RSA 明文长度无效。");

        BigInteger clientPublic = new(plain.AsSpan(4), true, true);

        if (clientPublic <= 1 || clientPublic >= Prime) throw new InvalidDataException("DH 公钥无效。");
        for (int attempt = 0; attempt < 64; attempt++)
        {
            BigInteger secret = new BigInteger(RandomNumberGenerator.GetBytes(64), true, true) % (Prime - 2) + 1;
            byte[] shared = FixedInteger(BigInteger.ModPow(clientPublic, secret, Prime));

            // The client hashes the shortest big-integer representation with MD5. Exclude leading zeros for compatibility with the fixed 64-byte implementation.
            if (shared[0] == 0) continue;

            byte[] key = MD5.HashData(shared);
            byte[] publicKey = FixedInteger(BigInteger.ModPow(2, secret, Prime));
            byte[] ack = new Wire().U32(unchecked(Wire.Read32(plain) + 1)).Bytes(new byte[16]).ToArray();
            using var aes = Aes.Create();

            aes.Key = key;

            byte[] encryptedAck = aes.EncryptCbc(ack, LegacyCrypto.ApIv, PaddingMode.PKCS7);
            byte[] signature = rsa.SignData(new Wire().Bytes(publicKey).Bytes(encryptedAck).ToArray(), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            byte[] body = new Wire().U16(0).U32(0).U16((ushort)encryptedAck.Length).U8(64).Bytes(publicKey).Bytes(encryptedAck).Bytes(signature).ToArray();
            byte[] response = new Wire().Bytes("HNNW"u8.ToArray()).U16((ushort)body.Length).U16(1).U32(0).U16(2).Bytes(body).ToArray();

            return (response, key);
        }
        throw new CryptographicException("无法生成兼容客户端的 DH 共享值。");
    }

    public static byte[] AccountResult(uint sequence, uint uid, uint timestamp)
    {
        byte[] ticket = "LOCAL_TICKET_001"u8.ToArray();
        byte[] body = new Wire().U32(0).U32(0).String("").U32(uid).U32(timestamp).U16((ushort)ticket.Length).Bytes(ticket).ToArray();

        return new Wire().U16((ushort)body.Length).U16(1).U32(sequence).U16(4).Bytes(body).ToArray();
    }

    public static byte[] ApFrame(byte[] key, byte[] plain)
    {
        byte[] encrypted = LegacyCrypto.ApEncrypt(key, plain);

        return new Wire().Bytes("HNNW"u8.ToArray()).U16(checked((ushort)encrypted.Length)).Bytes(encrypted).ToArray();
    }

    private static byte[] FixedInteger(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(true, true), result = new byte[64];

        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }
}
