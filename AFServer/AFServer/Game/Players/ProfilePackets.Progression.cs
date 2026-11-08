namespace AFServer.AssaultFire.Game;

// AF2014 proto_c2zn.tdr uses a four-byte achievement result, 27-byte details, and 27 fixed statistic entries; do not mix later-version layouts.
internal static partial class ProfilePackets
{
    internal static TdrWriter AchievementList(PlayerState player)
    {
        var definitions = ProgressionCatalog.Instance.Achievements;
        var writer = new TdrWriter().U32(GameWire.AchievementSuccess).U32(definitions.Count);

        foreach (var definition in definitions)
        {
            var completed = player.Progression.Achievement(definition.Id);

            writer.U32(completed.Id).U8(completed.Level).U16(completed.Flags);
        }
        return writer;
    }

    internal static TdrWriter AchievementDetails(ulong uin, byte group, PlayerState? player,
        IReadOnlyList<AchievementDefinition> definitions, int result = GameWire.AchievementSuccess)
    {
        if (definitions.Count > 100) throw new InvalidDataException("成就详情单包不能超过一百项。");

        var writer = new TdrWriter().U32(result).U64(uin).U8(group).U32(player is null ? 0 : definitions.Count);

        if (player is not null)
            foreach (var definition in definitions) WriteAchievement(writer, player.Progression.Achievement(definition.Id));
        return writer;
    }

    internal static TdrWriter AchievementStatistics(ulong uin, PlayerState? player)
    {
        var statistics = new uint[ProgressionCatalog.GroupCount];
        var latest = new List<CompletedAchievement>();

        if (player is not null)
        {
            foreach (var definition in ProgressionCatalog.Instance.Achievements)
            {
                var completed = player.Progression.Achievement(definition.Id);

                if ((completed.Flags & 1) == 0) continue;

                var count = checked((uint)definition.Tiers.Count(tier => tier.Level <= completed.Level));

                // The native C009 handler rolls subcategory counts into parents and then the root. Send only direct-category counts here.
                // The UI denominator excludes hidden AchiSortID=0 entries. Exclude them from statistics too, while retaining every achievement in the unlock list.
                if (!definition.Highest.Hidden) statistics[definition.Group] += count;
                latest.Add(completed);
            }
        }

        var recent = latest.OrderByDescending(completed => completed.CompletedAt).ThenByDescending(completed => completed.Id).Take(10).ToArray();
        var writer = new TdrWriter().U32(player is null ? GameWire.AchievementWrongData : GameWire.AchievementSuccess)
            .U64(uin).U32(player?.Progression.AchievementScore ?? 0);

        foreach (var count in statistics) writer.U32(count);
        writer.U32(recent.Length);
        foreach (var completed in recent) WriteAchievement(writer, completed);
        return writer;
    }

    internal static TdrWriter AchievementHistory(ulong uin, uint id, PlayerState? player)
    {
        var definition = ProgressionCatalog.Instance.ById.GetValueOrDefault(id);
        var completed = player is not null && definition is not null ? player.Progression.Achievement(id) : null;
        var tiers = completed is null ? [] : definition!.Tiers.Where(tier => tier.Level <= completed.Level).ToArray();
        var writer = new TdrWriter().U32(completed is null ? GameWire.AchievementWrongData : GameWire.AchievementSuccess)
            .U64(uin).U32(id).U32(tiers.Length);

        foreach (var tier in tiers) writer.U8(tier.Level).U64(completed!.CompletedAt);
        return writer;
    }

    internal static TdrWriter NameCard(ulong uin, PlayerState? player, int? noticeId = null)
    {
        var writer = new TdrWriter().U32(noticeId ?? (player is null ? GameWire.AchievementWrongData : GameWire.AchievementSuccess)).U64(uin);

        // Without a clan, all three badge components use the native -1 sentinel. CardInfo ends with PrivacyFlags, not ReLiveFlags.
        return writer.U32(-1).U32(-1).U32(-1).U32(player?.Progression.CardId ?? ProgressionCatalog.DefaultCardId)
            .U32(player?.Progression.Experience ?? 0).String("", 32).U8(0).String(player?.Nickname ?? "", 32)
            .U16(player?.Progression.PrivacyFlags ?? 0);
    }

    private static void WriteAchievement(TdrWriter writer, CompletedAchievement completed)
        => writer.U32(completed.Id).U8(completed.Level).U64(completed.CompletedAt).U64(completed.Cumulate)
            .U16(completed.Flags).U32(0);
}
