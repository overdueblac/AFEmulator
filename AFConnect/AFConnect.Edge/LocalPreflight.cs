using System.Security.Cryptography;
using AFConnect.EDGE.Client;

namespace AFConnect.EDGE;

/// <summary>
/// Separates local client validation from remote-server startup. Connect does not hold the
/// game-authentication private key.
/// </summary>
public static class LocalPreflight
{
    /// <summary>
    /// Checks required player files and the separately deployed authentication public key. Any configured
    /// fingerprint must match exactly.
    /// </summary>
    public static async Task CheckAsync(ConnectOptions options, CancellationToken token)
    {
        ClientCompatibility.ValidateGameRoot(options.GamePath);

        byte[] bytes = await File.ReadAllBytesAsync(options.LauncherPublicKeyPath, token);

        if (!string.IsNullOrWhiteSpace(options.GamePublicKeySha256) &&
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(options.GamePublicKeySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("启动器公钥与配置指纹不一致。");

        using var rsa = RSA.Create();

        rsa.ImportFromPem(System.Text.Encoding.ASCII.GetString(bytes));
    }
}
