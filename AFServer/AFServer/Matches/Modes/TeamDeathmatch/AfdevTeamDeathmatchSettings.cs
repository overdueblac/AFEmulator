using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Resolves frozen settings from original PH MatchSettings and this build's DefaultEngine ordinary-team
/// rules.
/// </summary>
internal sealed record AfdevTeamDeathmatchSettings(int Rule, int Value, int RespawnTime)
{
    internal int GoalScore => Rule == 3 ? Value : 0;
    internal int TimeSeconds => Rule == 1 ? Value * 60 : 1200;

    internal static AfdevTeamDeathmatchSettings Read(NativeMatchDescriptor descriptor)
    {
        if (!AfdevMapCatalog.SupportsTeamDeathmatch(descriptor.ModeId, descriptor.MapId) || descriptor.SubModeId != 0)
            throw new InvalidDataException("普通团队战不接受其他模式、地图或子模式。");
        if (descriptor.MatchSettingsWire.IsDefaultOrEmpty)
            return new(3, 100, 0); // For host diagnostics without network settings only. Production lobbies must supply complete original settings.

        var reader = new TdrReader(descriptor.MatchSettingsWire.ToArray());

        if (reader.U32() != descriptor.ModeId || reader.U16() != descriptor.MapId ||
            reader.String(32) != descriptor.MapString || reader.U32() != descriptor.SubModeId ||
            reader.U32() != descriptor.RoomFlags)
            throw new InvalidDataException("团队战原始设置与冻结描述不一致。");

        int rule = reader.U16(), value = reader.U16(), respawn = reader.U16();

        reader.Read(4); // Preserve RecodeType / LiveDelaySec verbatim; they are not match rules.
        reader.End();
        if ((rule == 1 && value is not (5 or 8 or 10 or 12)) ||
            (rule == 3 && value is not (40 or 60 or 80 or 100 or 150 or 200)) ||
            rule is not (1 or 3) || respawn is not (0 or 3 or 5 or 10))
            throw new InvalidDataException("团队战时间、杀敌数或复活时间超出本版客户端规则。");

        return new(rule, value, respawn);
    }

    internal static void ValidateRoster(MatchStartSnapshot start)
    {
        TeamDeathmatchSeats.Validate(start.FighterCapacity, start.Descriptor.ObserverCapacity);
        _ = Read(start.Descriptor);

        var fighters = TeamDeathmatchSeats.Fighters(start.FighterCapacity).ToHashSet();
        var observers = TeamDeathmatchSeats.Observers(start.FighterCapacity, start.Descriptor.ObserverCapacity).ToHashSet();

        if (start.Members.Select(member => member.SeatIndex).Distinct().Count() != start.Members.Length ||
            start.Members.Any(member => !(member.Observer ? observers : fighters).Contains(member.SeatIndex)))
            throw new InvalidDataException("团队战冻结席位重复或超出本局两队/观察席范围。");
    }
}
