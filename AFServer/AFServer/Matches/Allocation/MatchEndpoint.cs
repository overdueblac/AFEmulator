namespace AFServer.AssaultFire.Game;

/// <summary>
/// Match address, port, and shared DS key returned by an allocator. The shared key does not establish
/// individual player admission.
/// </summary>
public sealed record MatchEndpoint(string Address, ushort Port, byte[] Key);
