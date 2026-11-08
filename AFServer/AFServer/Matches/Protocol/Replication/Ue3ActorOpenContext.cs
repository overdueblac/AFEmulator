namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Caller-established result from trusted resolution/inheritance, never guessed from package names,
/// reference indices, or network data.
/// </summary>
public enum Ue3ActorOpenResolvedType { Unresolved, NonActor, Actor, PlayerController }

/// <summary>
/// Resolved Actor-open object snapshot. ObjectFlags belong to the referenced object; ActorFlagsAtB4 maps
/// native +0xB4. Both derive from effective actual state, or archetype state on creation, never
/// unsynthesized candidate exports. ActorClassObjectId identifies the actual resolved/new Actor class;
/// PlayerController typing must agree with native +0x330 dispatch. References apply only within the same
/// connection/PackageMap generation. This type states caller prerequisites without loading, visibility
/// checks, spawning, or proving trust.
/// </summary>
public sealed record Ue3ActorOpenContext(Ue3ObjectReference ObjectReference,
    Ue3ActorOpenResolvedType ResolvedType, long ActorClassObjectId, ulong ObjectFlags, uint ActorFlagsAtB4)
{
    /// <summary>
    /// Native 0x011F8869..0x011F8875 includes creation position when the low 32 ObjectFlags bits intersect
    /// 0x600.
    /// </summary>
    public bool HasInitialLocation => (ObjectFlags & 0x600) != 0;

    /// <summary>
    /// Reads this bit on creation only. An ordinary resolved Actor has no initial rotation even if +0xB4
    /// bit 0 is set.
    /// </summary>
    public bool HasInitialRotation => HasInitialLocation && (ActorFlagsAtB4 & 1) != 0;

    public bool HasPlayerControllerIndex => ResolvedType == Ue3ActorOpenResolvedType.PlayerController;
}

/// <summary>
/// Contains only actual wire open fields. Missing initial rotation stays null rather than inventing native
/// creation's zero rotation. PlayerControllerIndex is the eight-bit connection/split-screen index at
/// +0x399: zero means main connection; callers resolve nonzero child connections.
/// </summary>
public sealed record Ue3ActorOpenHeader(Ue3ObjectReference ObjectReference,
    Ue3QuantizedVector? InitialLocation, Ue3Rotator? InitialRotation, byte? PlayerControllerIndex);

/// <summary>
/// Unapplied Actor-open header/fields. Successful parsing proves neither Actor creation nor RPC
/// authorization.
/// </summary>
public sealed record Ue3ActorOpenPayload(Ue3ActorOpenHeader Header, IReadOnlyList<Ue3ActorFieldUpdate> Fields);
