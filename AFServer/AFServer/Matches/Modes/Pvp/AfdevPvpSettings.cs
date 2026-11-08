using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Frozen ordinary-PvP rules, retaining original lobby settings and expanding target scores/round counts
/// only at the native boundary.
/// </summary>
internal sealed record AfdevPvpSettings(int Rule, int Value, int RespawnTime, bool Bomb)
{
    internal int GoalScore => Bomb || Rule == 3 ? Value : 0;
    internal int TimeSeconds => Bomb ? 150 : Rule == 1 ? Value * 60 : 1200;

    internal static AfdevPvpSettings Read(NativeMatchDescriptor descriptor)
    {
        if (AfdevMapCatalog.SupportsTeamDeathmatch(descriptor.ModeId, descriptor.MapId))
        {
            var team = AfdevTeamDeathmatchSettings.Read(descriptor);

            return new(team.Rule, team.Value, team.RespawnTime, false);
        }

        bool bomb = AfdevMapCatalog.SupportsBomb(descriptor.ModeId, descriptor.MapId);

        if ((!bomb && !AfdevMapCatalog.SupportsIndividual(descriptor.ModeId, descriptor.MapId)) ||
            (bomb ? descriptor.SubModeId != 0 : descriptor.SubModeId != 0 && descriptor.SubModeId is not (>= 8192 and <= 8200)))
            throw new InvalidDataException("PvP 模式、地图或子模式不属于已接入范围。");
        if (descriptor.MatchSettingsWire.IsDefaultOrEmpty)
            return bomb ? new(2, 9, 0, true) : new(3, 30, 0, false);

        var reader = new TdrReader(descriptor.MatchSettingsWire.ToArray());

        if (reader.U32() != descriptor.ModeId || reader.U16() != descriptor.MapId ||
            reader.String(32) != descriptor.MapString || reader.U32() != descriptor.SubModeId ||
            reader.U32() != descriptor.RoomFlags)
            throw new InvalidDataException("PvP 原始设置与冻结描述不一致。");

        int rule = reader.U16(), value = reader.U16(), respawn = reader.U16();

        reader.Read(4);
        reader.End();
        if (bomb ? rule != 2 || value is not (5 or 7 or 9 or 11 or 13) || respawn != 0
            : (rule == 1 && value is not (5 or 8 or 10 or 12)) ||
              (rule == 3 && value is not (10 or 20 or 30)) || rule is not (1 or 3) || respawn is not (0 or 3 or 5 or 10))
            throw new InvalidDataException("PvP 回合、时间、杀敌数或复活设置超出本版客户端规则。");

        return new(rule, value, respawn, bomb);
    }

    internal static void ValidateRoster(MatchStartSnapshot start)
    {
        _ = Read(start.Descriptor);
        if (AfdevMapCatalog.UsesTeamSeats(start.Descriptor.ModeId))
            TeamDeathmatchSeats.Validate(start.FighterCapacity, start.Descriptor.ObserverCapacity);
        else if (start.Descriptor.ObserverCapacity < 0 || start.FighterCapacity + start.Descriptor.ObserverCapacity > 32)
            throw new InvalidDataException("个人战参战与观察席合计不能超过 32。");

        var fighters = (AfdevMapCatalog.UsesTeamSeats(start.Descriptor.ModeId)
            ? TeamDeathmatchSeats.Fighters(start.FighterCapacity) : Enumerable.Range(0, start.FighterCapacity)).ToHashSet();
        var observers = (AfdevMapCatalog.UsesTeamSeats(start.Descriptor.ModeId)
            ? TeamDeathmatchSeats.Observers(start.FighterCapacity, start.Descriptor.ObserverCapacity)
            : Enumerable.Range(start.FighterCapacity, start.Descriptor.ObserverCapacity)).ToHashSet();

        if (start.Members.Select(member => member.SeatIndex).Distinct().Count() != start.Members.Length ||
            start.Members.Any(member => !(member.Observer ? observers : fighters).Contains(member.SeatIndex)))
            throw new InvalidDataException("PvP 冻结席位重复或超出本局参战/观察席范围。");
    }
}
