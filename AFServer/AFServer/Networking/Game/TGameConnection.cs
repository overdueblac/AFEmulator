using System.Security.Cryptography;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Networking;

/// <summary>
/// Each TCP session owns its key, serializes writes, and reads by length to handle coalesced/fragmented
/// traffic.
/// </summary>
public sealed class TGameConnection : IDisposable
{
    private readonly Stream stream;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private TGameCryptoState state;
    public TGameAuth Auth { get; }

    private TGameConnection(Stream stream, TGameAuth auth, TGameCryptoState state)
    {
        this.stream = stream;
        Auth = auth;
        this.state = state;
    }

    public async ValueTask SendAsync(byte[] applicationPayload, CancellationToken token = default)
    {
        await sendLock.WaitAsync(token);
        try { await stream.WriteAsync(TGameProtocol.Application(applicationPayload, state), token); }
        finally { sendLock.Release(); }
    }

    public static async Task RunAsync(Stream stream,
        Func<TGameAuth, CancellationToken, ValueTask<TGameCryptoState>> keyResolver,
        Func<TGameConnection, byte[], CancellationToken, ValueTask> handler,
        CancellationToken token,
        Action<TGameConnection>? disconnected = null,
        Action<uint>? authenticateTacc = null)
    {
        byte[] first = await LauncherConnections.ReadAsync(stream, 2, token);

        if (first[1] == 0x0c)
        {
            byte[] header = new Wire().Bytes(first).Bytes(await LauncherConnections.ReadAsync(stream, 7, token)).ToArray();
            int headLength = header[3];
            uint bodyLength = Wire.Read32(header, 5);

            if (header[2] != 3 || headLength < 26 || bodyLength > 0x400000) throw new InvalidDataException("ROLE AUTH 帧无效。");

            byte[] rest = await LauncherConnections.ReadAsync(stream, checked(headLength - 9 + (int)bodyLength), token);
            byte[] frame = new Wire().Bytes(header).Bytes(rest).ToArray();
            uint uin = Wire.Read32(frame, 21);

            authenticateTacc?.Invoke(uin);
            await stream.WriteAsync(TGameProtocol.TaccResponse(uin == 0 ? 10001u : uin, unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())), token);
            return;
        }

        byte[] authFrame = await ReadFrameAsync(stream, token, first);
        TGameAuth auth = TGameProtocol.ParseAuth(authFrame);
        TGameCryptoState initialState = await keyResolver(auth, token);
        using var connection = new TGameConnection(stream, auth, initialState);

        try
        {
            await stream.WriteAsync(TGameProtocol.Syn(initialState), token);

            byte[] synAck = await ReadFrameAsync(stream, token);

            if (synAck.Length < 13 || synAck[2] != 9 || synAck[12] > synAck.Length - 13) throw new InvalidDataException("SYNACK 帧无效。");

            byte[] echo = LegacyCrypto.Decrypt(synAck[13..(13 + synAck[12])], initialState.Key, initialState.Mode);

            if (!CryptographicOperations.FixedTimeEquals(echo, TGameProtocol.Challenge)) throw new InvalidDataException("SYNACK 挑战不匹配。");
            await stream.WriteAsync(TGameProtocol.ChangeKey(initialState), token);
            CryptographicOperations.ZeroMemory(initialState.Key);
            connection.state = new((byte[])TGameProtocol.SessionKey.Clone(), initialState.Mode);
            while (!token.IsCancellationRequested)
            {
                byte[] frame = await ReadFrameAsync(stream, token);

                if (frame[2] == 0x0d) return;
                if (frame[2] is not (0 or 2)) throw new InvalidDataException($"不支持的应用传输命令 0x{frame[2]:x2}。");

                int head = checked((int)Wire.Read32(frame, 4));

                if (head == frame.Length) continue;

                byte[] plain = LegacyCrypto.Decrypt(frame[head..], connection.state.Key, connection.state.Mode);

                await handler(connection, plain, token);
            }
        }
        catch (EndOfStreamException) { }
        finally { disconnected?.Invoke(connection); }
    }

    /// <summary>
    /// Strict frame-length bounds prevent oversized client lengths from causing unbounded allocation.
    /// </summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token, byte[]? prefix = null)
    {
        prefix ??= [];

        byte[] header = new Wire().Bytes(prefix).Bytes(await LauncherConnections.ReadAsync(stream, 12 - prefix.Length, token)).ToArray();

        if (header[0] != 0x55 || header[1] != 0x0e) throw new InvalidDataException("TGame 帧标记无效。");

        uint head = Wire.Read32(header, 4), body = Wire.Read32(header, 8);
        ulong total = (ulong)head + body;

        if (head < 12 || head > 0x10000 || total > 0x400000) throw new InvalidDataException("TGame 帧长度超出范围。");
        return new Wire().Bytes(header).Bytes(await LauncherConnections.ReadAsync(stream, (int)total - 12, token)).ToArray();
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(state.Key);
        sendLock.Dispose();
    }
}
