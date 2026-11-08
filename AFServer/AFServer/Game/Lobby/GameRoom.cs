namespace AFServer.AssaultFire.Game;

/// <summary>
/// Mutable lobby-room state maintained by GameDispatcher under the business transaction lock, never used
/// directly as a native-match snapshot.
/// </summary>
public sealed class GameRoom
{
    public ulong RoomId { get; init; }
    /// <summary>
    /// Identifies room-creation requests resent within eight seconds; does not represent match start time.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public ushort DisplayId { get; init; }
    /// <summary>
    /// The owner account in the protocol snapshot. Owner authority depends on exact OwnerSession identity,
    /// never merged connections with this value.
    /// </summary>
    public ulong OwnerUin { get; set; }
    internal GameSession? OwnerSession { get; set; }
    public string Name { get; set; } = "";
    public string Password { get; set; } = "";
    public uint ModeId { get; set; }
    public ushort MapId { get; set; }
    public string MapString { get; set; } = "";
    public uint SubModeId { get; set; }
    public uint RoomFlags { get; set; }
    public byte[] MatchSettingsWire { get; set; } = [];
    public byte FighterCapacity { get; set; }
    public byte ObserverCapacity { get; set; }
    public bool Started { get; set; }
    public MatchEndpoint? Endpoint { get; set; }
    /// <summary>
    /// Retains pending-cleanup state after release failure. Reallocation and next-round grants remain
    /// blocked until cleanup completes.
    /// </summary>
    internal bool MatchReleasePending { get; set; }
    /// <summary>
    /// Increments when members, settings, or preparation operations change, rejecting stale publication
    /// after slow startup.
    /// </summary>
    internal long StartVersion { get; set; }
    public List<RoomMember> Members { get; } = [];
}
