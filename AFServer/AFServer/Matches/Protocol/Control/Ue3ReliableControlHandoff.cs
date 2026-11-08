namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// One-time ownership token for reliable control state, retaining packet/sequence numbers, reordering
/// buffers, ACKs, retransmission history, and clocks. Grants no authentication, membership, or UDP binding.
/// Cannot reuse after either successful or failed consumption; the caller disposes unused tokens.
/// </summary>
public sealed class Ue3ReliableControlHandoff : IDisposable
{
    private Ue3ReliablePeer? peer;

    internal Ue3ReliableControlHandoff(Ue3ReliablePeer peer, int incomingSequence, int outgoingSequence)
    {
        this.peer = peer;
        NextIncomingSequence = incomingSequence;
        NextOutgoingSequence = outgoingSequence;
    }

    internal int NextIncomingSequence { get; }
    internal int NextOutgoingSequence { get; }

    /// <summary>
    /// Whether the token was consumed/disposed, not whether a later replication connection remains alive.
    /// </summary>
    public bool IsConsumed => Volatile.Read(ref peer) is null;

    internal Ue3ReliablePeer Consume()
        => Interlocked.Exchange(ref peer, null)
            ?? throw new InvalidOperationException("可靠状态令牌已被消费或释放。");

    /// <summary>
    /// Closes reliable state still owned by this token only. Disposal after consumption leaves the new
    /// connection owner intact.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref peer, null)?.Disconnect("control-handoff-disposed");
}
