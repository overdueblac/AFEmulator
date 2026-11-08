namespace AFConnect.Proxy;

/// <summary>CONNECT v2 frame kinds define boundaries without granting control or datagram access.</summary>
public enum TunnelFrameKind : byte
{
    /// <summary>Unmodified game TCP bytes.</summary>
    Game = 1,

    /// <summary>A control payload requiring validation by the current connection's business handler.</summary>
    Control = 2,

    /// <summary>
    /// A datagram payload requiring validation by the current connection's allocation adapter.
    /// </summary>
    Datagram = 3
}

/// <summary>
/// Handles auxiliary frames serially for the current logical stream. Payloads are valid only during the
/// callback and are zeroed afterward; copying and clearing retained data is the handler's responsibility.
/// Handlers must honor cancellation and must not treat stream IDs, UINs, or endpoint fields as business
/// authorization.
/// </summary>
public delegate ValueTask TunnelFrameHandler(TunnelFrameKind kind, ReadOnlyMemory<byte> payload,
    CancellationToken cancellationToken);
