using System.Collections.Immutable;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Immutable start-time room snapshot, retaining no lobby sessions, mutable member lists, or persistence
/// objects.
/// </summary>
public sealed record NativeMatchDescriptor(
    ulong RoomId,
    ulong OwnerUin,
    uint ModeId,
    ushort MapId,
    string MapString,
    uint SubModeId,
    uint RoomFlags,
    byte FighterCapacity,
    byte ObserverCapacity,
    ImmutableArray<byte> MatchSettingsWire,
    ImmutableArray<NativeMatchMember> Members)
{
    /// <summary>
    /// Create the snapshot under the lobby-room lock. Later room changes cannot alter this round's
    /// parameters.
    /// </summary>
    public static NativeMatchDescriptor Capture(GameRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new(room.RoomId, room.OwnerUin, room.ModeId, room.MapId, room.MapString,
            room.SubModeId, room.RoomFlags, room.FighterCapacity, room.ObserverCapacity,
            [.. room.MatchSettingsWire],
            [.. room.Members.Select(member => new NativeMatchMember(member.Session.Uin, member.SeatIndex, member.Observer))]);
    }
}

/// <summary>Start-time member identity and seat. The concrete protocol still verifies UDP identity.</summary>
public sealed record NativeMatchMember(ulong Uin, ushort SeatIndex, bool Observer);
