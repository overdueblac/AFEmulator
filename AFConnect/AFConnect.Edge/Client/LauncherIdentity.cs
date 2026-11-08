using System.Security.Cryptography;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Login identity returned by a validated server. Session keys and tickets remain in memory and are zeroed
/// on disposal.
/// </summary>
/// <param name="uin">The nonzero account authorized by the server.</param>
/// <param name="timestamp">The server timestamp required by native login.</param>
/// <param name="ticket">The ticket array transferred to this object and zeroed on disposal.</param>
/// <param name="key">The 16-byte key array transferred to this object and zeroed on disposal.</param>
public sealed class LauncherIdentity(uint uin, uint timestamp, byte[] ticket, byte[] key) : IDisposable
{
    /// <summary>
    /// The account currently authorized by the server. The compatibility server still selects it through
    /// ActiveUin.
    /// </summary>
    public uint Uin { get; } = uin;

    internal uint Timestamp { get; } = timestamp;
    internal byte[] Ticket { get; } = ticket;
    internal byte[] Key { get; } = key;

    /// <summary>
    /// Clears the launch ticket and authentication-channel key, preventing further game-session creation.
    /// </summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Ticket);
        CryptographicOperations.ZeroMemory(Key);
    }
}

/// <summary>
/// A launchable directory entry whose endpoint must target the ROLE listener already owned by this Connect
/// instance.
/// </summary>
/// <param name="Name">The server-directory display name.</param>
/// <param name="Version">The version text published by the server.</param>
/// <param name="Id">The native server identifier.</param>
/// <param name="Host">The local ROLE proxy host.</param>
/// <param name="Port">The local ROLE port paired with the server.</param>
public sealed record LauncherServer(string Name, string Version, uint Id, string Host, int Port);
