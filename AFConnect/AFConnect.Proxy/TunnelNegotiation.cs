namespace AFConnect.Proxy;

/// <summary>
/// The negotiated transport version and server-assigned logical stream ID. The ID provides isolation, not
/// authentication credentials.
/// </summary>
public sealed record TunnelNegotiation(int Version, Guid StreamId)
{
    /// <summary>The compatible protocol that sends game bytes directly after the original handshake.</summary>
    public const int RawVersion = 1;

    /// <summary>
    /// The protocol with separate game, control, and datagram boundaries. Business handlers must still
    /// explicitly authorize auxiliary frames.
    /// </summary>
    public const int FramedVersion = 2;
}

/// <summary>A handshake request with validated formatting. Server still performs authentication.</summary>
public sealed record TunnelHandshake(int Version, TunnelRequest Request);
