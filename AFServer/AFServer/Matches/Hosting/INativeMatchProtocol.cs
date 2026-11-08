using System.Net;

namespace AFServer.AssaultFire.Matches;

/// <summary>Sends one complete UDP datagram to the specified peer within the host's length limit.</summary>
public delegate ValueTask NativeMatchSender(ReadOnlyMemory<byte> datagram, IPEndPoint peer, CancellationToken cancellationToken);

/// <summary>
/// Creates the native match protocol for one room. Throw if required protocol or world data is unavailable.
/// Empty handlers, echo handlers, and listener-only implementations must not claim a match is usable.
/// </summary>
public interface INativeMatchProtocolFactory
{
    /// <summary>
    /// Maximum complete UDP datagram length permitted by the protocol, explicitly set by the factory. AF DS
    /// uses NativeDsDatagramCodec.MaximumDatagramBytes (516); the IPv4 limit cannot replace the protocol
    /// boundary.
    /// </summary>
    int MaximumDatagramBytes { get; }

    /// <summary>
    /// sessionKey is this round's 16-byte DS key returned through A11A. Valid only during Create. Protocols
    /// retaining it must copy it and clear their copy in DisposeAsync.
    /// </summary>
    INativeMatchProtocol Create(NativeMatchDescriptor match, ReadOnlySpan<byte> sessionKey);
}

/// <summary>
/// Protocol/simulation boundary for one room. The host serializes datagram and clock callbacks; await all
/// I/O and propagate cancellation. Datagram memory is valid only during the callback; copy retained data
/// and never use the send delegate afterward. Protocols explicitly reject invalid client input. Escaping
/// exceptions fail the match.
/// </summary>
public interface INativeMatchProtocol : IAsyncDisposable
{
    ValueTask HandleDatagramAsync(ReadOnlyMemory<byte> datagram, IPEndPoint peer,
        NativeMatchSender send, CancellationToken cancellationToken);

    /// <summary>
    /// Scheduled roughly every 50 milliseconds. elapsed is actual time since the previous clock dispatch,
    /// including processing delays.
    /// </summary>
    ValueTask TickAsync(TimeSpan elapsed, NativeMatchSender send, CancellationToken cancellationToken);
}
