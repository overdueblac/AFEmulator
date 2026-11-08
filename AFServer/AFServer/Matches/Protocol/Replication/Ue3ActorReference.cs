namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Actor reference registered by this sending connection, available only after open confirmation. Cannot
/// reconstruct by the same number across connections. Retaining it does not extend channel lifetime; every
/// enqueue rechecks. Proves no authentication, world-object liveness, or replication relevance.
/// </summary>
public sealed class Ue3ActorReference
{
    internal Ue3ActorReference(int channelIndex, long actorId)
    {
        ChannelIndex = channelIndex;
        ActorId = actorId;
    }

    /// <summary>Channel number in this connection, diagnostic only; cannot regrant a reference.</summary>
    public int ChannelIndex { get; }

    /// <summary>Actor identity supplied by the trusted world caller at channel creation.</summary>
    public long ActorId { get; }
}

/// <summary>
/// Sender-level object-property value. Null Reference explicitly clears the property. The sender validates
/// declared type/current channel before wire conversion; do not pass directly to the low-level codec or use
/// for RPC.
/// </summary>
public sealed record Ue3ActorReferenceValue(Ue3ActorReference? Reference) : Ue3ActorValue;
