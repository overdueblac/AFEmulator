using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Frozen-roster boundary shared by integrated AFDEV modes. BIO separately validates its player-bot plan.
/// </summary>
internal static class AfdevMatchStartValidator
{
    internal static void Validate(MatchStartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCapacity(snapshot.Descriptor.ModeId, snapshot.Descriptor.MapId, snapshot.FighterCapacity);

        if (snapshot.Descriptor.ModeId == 0x2001)
            _ = AfdevSurvivalSettings.Difficulty(snapshot.Descriptor.SubModeId, snapshot.Descriptor.RoomFlags);
        else if (snapshot.Descriptor.ModeId == 0x2005)
            _ = AfdevTowerDefenseSettings.Difficulty(snapshot.Descriptor.SubModeId, snapshot.Descriptor.RoomFlags);
        else if (snapshot.Descriptor.ModeId == 0x2002)
            _ = AfdevDefenseSettings.Difficulty(snapshot.Descriptor.SubModeId, snapshot.Descriptor.RoomFlags);
        else if (snapshot.Descriptor.ModeId == 0x2003)
            _ = AfdevEncirclementSettings.Difficulty(snapshot.Descriptor.SubModeId, snapshot.Descriptor.RoomFlags);
        else if (AfdevMapCatalog.SupportsPvp(snapshot.Descriptor.ModeId, snapshot.Descriptor.MapId))
            AfdevPvpSettings.ValidateRoster(snapshot);

        if (snapshot.AllocationId == Guid.Empty || snapshot.RoomId == 0 || snapshot.Members.IsDefaultOrEmpty)
            throw new InvalidOperationException("原生开赛必须保留真实 allocation、房间和冻结成员。");

        int humanCount = snapshot.Members.Count(member => !member.Observer);

        if (humanCount < 1 || humanCount > snapshot.FighterCapacity)
            throw new InvalidOperationException("冻结快照必须至少有一名参战真人，且不能超过本局参战容量。");
        if (snapshot.Members.Any(member => member.Session.SessionId == Guid.Empty || member.Profile.Uin == 0 ||
                member.Session.Uin != member.Profile.Uin || string.IsNullOrWhiteSpace(member.Profile.Nickname)) ||
            snapshot.Members.Select(member => member.Session.SessionId).Distinct().Count() != snapshot.Members.Length ||
            snapshot.Members.Select(member => member.Profile.Uin).Distinct().Count() != snapshot.Members.Length ||
            snapshot.Descriptor.Members.IsDefault ||
            !snapshot.Descriptor.Members.SequenceEqual(snapshot.Members.Select(member =>
                new NativeMatchMember(member.Profile.Uin, member.SeatIndex, member.Observer))))
            throw new InvalidOperationException("真人名额必须来自相同冻结成员，且精确 Session、UIN、座位和观察者属性一致。");
    }

    internal static void ValidateCapacity(uint modeId, ushort mapId, int capacity)
    {
        _ = AfdevMapCatalog.Resolve(modeId, mapId);

        int maximum = modeId switch
        {
            0x1002 => mapId == 0x006B ? 16 : 24,
            0x2001 => AfdevSurvivalSettings.MaximumPlayers,
            0x2002 => AfdevDefenseSettings.MaximumPlayers(mapId),
            0x2003 => AfdevEncirclementSettings.MaximumPlayers,
            0x2005 => 4,
            _ => 16
        };

        if (capacity < 1 || capacity > maximum)
            throw new InvalidOperationException($"模式 0x{modeId:X4} 参战容量必须为 1–{maximum}，实际为 {capacity}。");
        if (AfdevMapCatalog.UsesTeamSeats(modeId)) TeamDeathmatchSeats.Validate(capacity, 0);
    }
}
