using System.Security.Cryptography;
using AFServer.AssaultFire.Protocol;

namespace AFServer.AssaultFire.Networking;

/// <summary>
/// Separates launcher-connection read boundaries from protocol calculations. The listener host owns stream
/// lifetime.
/// </summary>
public static class LauncherConnections
{
    public static async Task HandleVersionAsync(Stream stream, CancellationToken token, Action<string>? log = null)
    {
        byte[] request = await ReadAsync(stream, 65, token);
        byte[] response = LauncherProtocol.Version(request);

        log?.Invoke($"RX VERSION body={request.Length}B; TX body={response.Length}B result=OK");
        await stream.WriteAsync(response, token);
    }

    public static async Task HandleDirectoryAsync(Stream stream, string host, int rolePort, CancellationToken token, Action<string>? log = null)
    {
        byte[] request = await ReadAsync(stream, 26, token);
        byte[] response = LauncherProtocol.Directory(request, host, rolePort);

        log?.Invoke($"RX DIR body={request.Length}B; TX body={response.Length}B result=OK endpoint={host}:{rolePort}");
        await stream.WriteAsync(response, token);
    }

    public static async Task HandleAuthAsync(Stream stream, string rsaPemPath, uint uid, CancellationToken token, Action<string>? log = null)
    {
        using var rsa = RSA.Create();

        rsa.ImportFromPem(await File.ReadAllTextAsync(rsaPemPath, token));

        byte[] authRequest = await ReadAsync(stream, 214, token);

        var (response, key) = LauncherProtocol.Authenticate(authRequest, rsa);
        try
        {
            await stream.WriteAsync(response, token);
            log?.Invoke($"RX AUTH body={authRequest.Length}B; TX AUTH_ACK body={response.Length}B result=OK");

            byte[] request = LegacyCrypto.ApDecrypt(key, await ReadApAsync(stream, token));

            if (request.Length < 10 || Wire.Read16(request, 8) != 3) throw new InvalidDataException("AP VERIFY 请求无效。");

            byte[] result = LauncherProtocol.AccountResult(Wire.Read32(request, 4), uid, unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            byte[] responseFrame = LauncherProtocol.ApFrame(key, result);

            await stream.WriteAsync(responseFrame, token);
            log?.Invoke($"RX AUTH_VERIFY body={request.Length}B; TX ACCOUNT_RESULT body={result.Length}B result=OK UIN={uid}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { _ = await ReadApAsync(stream, timeout.Token); }
            catch (EndOfStreamException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        byte[] buffer = new byte[count];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await stream.ReadExactlyAsync(buffer, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("客户端读取超时。"); }
        return buffer;
    }

    private static async Task<byte[]> ReadApAsync(Stream stream, CancellationToken token)
    {
        byte[] header = await ReadAsync(stream, 6, token);

        if (!header.AsSpan(0, 4).SequenceEqual("HNNW"u8)) throw new InvalidDataException("AP 帧标记无效。");
        return await ReadAsync(stream, Wire.Read16(header, 4), token);
    }
}
