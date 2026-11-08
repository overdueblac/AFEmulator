using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AFServer.AssaultFire.Protocol;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Implements the existing VERSION, RSA/DH AUTH, and directory protocols directly. All network traffic
/// still passes through Connect's mTLS tunnel.
/// </summary>
public static class LauncherAuthentication
{
    private static readonly BigInteger Prime = new(Convert.FromHexString("BB2A43DF39322DAAC8C3B30C9F21E13F646B7234A846231038C087F3408A558DA8AA1912AE906DEF3E781E39FE172B203A6B8452056B3C9CB21237CAC3F5AA1B"), true, true);

    /// <summary>
    /// Runs the version probe, verifies the server signature, and obtains the real account. Arbitrary
    /// text-box input does not constitute password authentication.
    /// </summary>
    public static async Task<LauncherIdentity> LoginAsync(ConnectOptions options, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;

        using (var version = new TcpClient())
        {
            await version.ConnectAsync(IPAddress.Loopback, 9060, token);
            await version.GetStream().WriteAsync(new byte[65], token);

            byte[] response = await ReadAsync(version.GetStream(), 65, token);

            if (Wire.Read32(response) != 65) throw new InvalidDataException("版本服务响应无效。");
        }

        using var rsa = RSA.Create();

        rsa.ImportFromPem(await File.ReadAllTextAsync(options.LauncherPublicKeyPath, token));

        BigInteger secret = new BigInteger(RandomNumberGenerator.GetBytes(64), true, true) % (Prime - 2) + 1;
        byte[] publicValue = FixedInteger(BigInteger.ModPow(2, secret, Prime));
        uint sequence = unchecked((uint)RandomNumberGenerator.GetInt32(int.MaxValue));
        byte[] encrypted = rsa.Encrypt(new Wire().U32(sequence).Bytes(publicValue).ToArray(), RSAEncryptionPadding.Pkcs1);
        byte[] request = new byte[214];

        "HNNW"u8.CopyTo(request);
        request[4] = 0;
        request[5] = 208;
        encrypted.CopyTo(request, 86);

        using var client = new TcpClient();

        await client.ConnectAsync(IPAddress.Loopback, 8000, token);

        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(request, token);

        // The initial plaintext ACK length excludes the 8-byte AP envelope; subsequent encrypted-frame lengths include the entire ciphertext.
        byte[] ack = await ReadFrameAsync(stream, token, envelopeLength: 8);

        if (ack.Length < 8 + 9 + 64 + 32 + 128 || Wire.Read16(ack, 6) != 2 || Wire.Read16(ack, 8) != 0)
            throw new InvalidDataException("认证握手响应无效。");

        int ackSize = Wire.Read16(ack, 14);
        int publicSize = ack[16];

        if (publicSize != 64 || ackSize != 32 || ack.Length != 17 + publicSize + ackSize + 128)
            throw new InvalidDataException("认证签名字段长度无效。");

        byte[] serverPublic = ack.AsSpan(17, publicSize).ToArray();
        byte[] encryptedAck = ack.AsSpan(17 + publicSize, ackSize).ToArray();
        byte[] signature = ack.AsSpan(17 + publicSize + ackSize).ToArray();
        BigInteger serverValue = new(serverPublic, true, true);

        if (serverValue <= 1 || serverValue >= Prime ||
            !rsa.VerifyData(serverPublic.Concat(encryptedAck).ToArray(), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("服务端认证签名校验失败。");

        byte[] key = MD5.HashData(FixedInteger(BigInteger.ModPow(serverValue, secret, Prime)));
        bool transferred = false;

        try
        {
            using var aes = Aes.Create();

            aes.Key = key;

            byte[] confirmation = aes.DecryptCbc(encryptedAck, LegacyCrypto.ApIv, PaddingMode.PKCS7);

            if (confirmation.Length != 20 || Wire.Read32(confirmation) != unchecked(sequence + 1))
                throw new CryptographicException("服务端未确认本次握手随机数。");

            await stream.WriteAsync(LauncherProtocol.ApFrame(key, new Wire().U16(0).U16(1).U32(sequence).U16(3).ToArray()), token);

            byte[] result = LegacyCrypto.ApDecrypt(key, await ReadFrameAsync(stream, token));

            if (result.Length < 33 || Wire.Read16(result, 8) != 4 || Wire.Read32(result, 10) != 0 || Wire.Read32(result, 14) != 0)
                throw new InvalidDataException("服务端没有授权当前账号。");

            int textLength = checked((int)Wire.Read32(result, 18));
            int offset = checked(22 + textLength);

            if (textLength is < 1 or > 4096 || offset + 10 > result.Length)
                throw new InvalidDataException("账号响应长度无效。");

            uint uin = Wire.Read32(result, offset);
            uint timestamp = Wire.Read32(result, offset + 4);
            int ticketLength = Wire.Read16(result, offset + 8);

            if (uin == 0 || ticketLength is < 1 or > 128 || offset + 10 + ticketLength != result.Length)
                throw new InvalidDataException("账号票据长度无效。");

            var identity = new LauncherIdentity(uin, timestamp, result.AsSpan(offset + 10, ticketLength).ToArray(), key);

            transferred = true;
            CryptographicOperations.ZeroMemory(result);
            return identity;
        }
        finally
        {
            if (!transferred) CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Parses actual directory leaf nodes and requires the published ROLE endpoint to match the local proxy
    /// configuration.
    /// </summary>
    public static async Task<IReadOnlyList<LauncherServer>> ReadServersAsync(ConnectOptions options, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var client = new TcpClient();

        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        token = timeout.Token;
        await client.ConnectAsync(IPAddress.Loopback, 9010, token);
        await client.GetStream().WriteAsync(new byte[26], token);

        byte[] header = await ReadAsync(client.GetStream(), 26, token);
        int length = checked((int)Wire.Read32(header, 4));

        if (length is < 7 or > 65535) throw new InvalidDataException("区服目录长度无效。");

        byte[] body = await ReadAsync(client.GetStream(), length, token);
        var reader = new DirectoryReader(body);

        reader.U16();
        reader.Byte();

        int count = reader.U16();

        reader.U16();

        if (count is < 1 or > 256) throw new InvalidDataException("区服目录节点数无效。");

        var servers = new List<LauncherServer>();

        for (int index = 0; index < count; index++)
        {
            int leaf = reader.U16();
            uint id = reader.U32();

            reader.U32();
            reader.U32();

            string name = reader.String();

            reader.U32();

            if (leaf == 0)
            {
                reader.U32();
                reader.String();
                reader.Byte();
                reader.U16();
                continue;
            }

            string address = reader.String();
            string version = reader.String();

            reader.U32();
            reader.U32();
            reader.String();
            reader.Byte();
            reader.String();
            reader.U16();

            if (leaf != 1 || !Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "tcp" ||
                endpoint.Host != "127.0.0.1" || endpoint.Port != options.LocalRolePort)
                throw new InvalidDataException("区服 ROLE 端点与连接端配置不一致，请在两端配对设置 LocalRolePort。");

            servers.Add(new LauncherServer(name, version, id, endpoint.Host, endpoint.Port));
        }

        if (servers.Count == 0) throw new InvalidDataException("服务端没有提供可启动区服。");

        return servers;
    }

    private static byte[] FixedInteger(BigInteger value)
    {
        byte[] result = new byte[64];
        byte[] bytes = value.ToByteArray(true, true);

        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token, int envelopeLength = 0)
    {
        byte[] header = await ReadAsync(stream, 6, token);

        if (!header.AsSpan(0, 4).SequenceEqual("HNNW"u8)) throw new InvalidDataException("认证帧标记无效。");

        int length = Wire.Read16(header, 4);

        if (length is < 1 or > 32768) throw new InvalidDataException("认证帧长度无效。");

        return await ReadAsync(stream, length + envelopeLength, token);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        byte[] bytes = new byte[count];

        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private sealed class DirectoryReader(byte[] bytes)
    {
        private int position;

        internal byte Byte() => Take(1)[0];
        internal ushort U16() => Wire.Read16(Take(2));
        internal uint U32() => Wire.Read32(Take(4));

        internal string String()
        {
            uint length = U32();

            if (length is < 1 or > 4096) throw new InvalidDataException("目录字符串长度无效。");

            ReadOnlySpan<byte> text = Take((int)length);

            if (text[^1] != 0) throw new InvalidDataException("目录字符串缺少结束符。");

            return Encoding.ASCII.GetString(text[..^1]);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > bytes.Length - position) throw new InvalidDataException("目录响应不完整。");

            ReadOnlySpan<byte> value = bytes.AsSpan(position, count);

            position += count;
            return value;
        }
    }
}
