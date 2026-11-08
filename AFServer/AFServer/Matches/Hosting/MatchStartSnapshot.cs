using System.Collections.Immutable;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Immutable start parameters and exact connections, created only under the lobby transaction lock. Never
/// rereads mutable player saves afterward.
/// </summary>
public sealed record MatchStartSnapshot(Guid AllocationId, NativeMatchDescriptor Descriptor,
    ImmutableArray<MatchStartMember> Members)
{
    /// <summary>
    /// Optional frozen ordinary-BIO plan, adding no fictional players to membership, authentication grants,
    /// or MongoDB.
    /// </summary>
    public AfdevBioBotPlan? BioBotPlan { get; init; }

    /// <summary>Unique lobby-room ID. allocation identifies this round across all startup attempts.</summary>
    public ulong RoomId => Descriptor.RoomId;

    /// <summary>
    /// Participant capacity frozen with the room snapshot. Bot planning and native OPEN must consume this
    /// same value.
    /// </summary>
    public byte FighterCapacity => Descriptor.FighterCapacity;
}

/// <summary>
/// Frozen member connection, seat, and minimal character equipment. A reconnect with the same UIN cannot
/// replace Session.
/// </summary>
public sealed record MatchStartMember(GameSession Session, ushort SeatIndex, bool Observer, MatchPlayerSnapshot Profile);

/// <summary>
/// Read-only start data from the authoritative player aggregate, excluding wallets, authentication secrets,
/// and mutable JSON objects.
/// </summary>
public sealed record MatchPlayerSnapshot(ulong Uin, string Nickname, ulong CurrentRole, ulong CurrentBag,
    ImmutableArray<MatchEquipmentSnapshot> Equipment)
{
    /// <summary>
    /// Start-time revival-coin total for native login display only. Each deduction must freshly commit the
    /// authoritative player aggregate.
    /// </summary>
    public int ReviveCoinCount { get; init; }
}

/// <summary>
/// Minimal mapping of current character, backpack, and equipped items, without creating a second business
/// store.
/// </summary>
public sealed record MatchEquipmentSnapshot(ulong Gid, uint ItemId, ulong OwnerGid, byte Location);
