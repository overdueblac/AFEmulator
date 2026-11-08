using System.Collections.Frozen;
using System.Text.Json;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// The native AF2014 level, achievement, and calling-card catalog. Values ship with the executable and
/// require no client directory or development tools.
/// </summary>
internal sealed partial class ProgressionCatalog
{
    internal const uint DefaultCardId = 10000;
    internal const int GroupCount = 27;
    internal static ProgressionCatalog Instance { get; } = Load();

    internal uint MaximumLevel { get; }
    internal int MaximumExperience { get; }
    internal uint TotalScore { get; }
    internal IReadOnlyList<AchievementDefinition> Achievements { get; }
    internal FrozenDictionary<uint, AchievementDefinition> ById { get; }
    internal FrozenDictionary<uint, AchievementTier> Cards { get; }
    internal FrozenDictionary<ushort, AchievementAward> Awards { get; }

    private ProgressionCatalog(Seed seed)
    {
        if (seed.SchemaVersion != 1 || seed.MaxLevel == 0 || seed.MaxExperience <= 0 || seed.Achievements.Length == 0)
            throw new InvalidDataException("角色成长目录的版本或等级范围无效。");

        var achievements = seed.Achievements.GroupBy(tier => tier.Id).OrderBy(group => group.Key)
            .Select(group => new AchievementDefinition(group.OrderBy(tier => tier.Level).ToArray())).ToArray();

        foreach (var achievement in achievements) achievement.Validate();
        foreach (var award in seed.Awards)
        {
            if (award.Level is < 1 or > 32 || award.Items.Length is < 1 or > 5 ||
                award.Items.Any(item => item.ItemId == 0 || item.Hours == 0))
                throw new InvalidDataException("成就奖励目录超出原生协议范围。");
        }

        MaximumLevel = seed.MaxLevel;
        MaximumExperience = seed.MaxExperience;
        TotalScore = checked((uint)seed.Achievements.Sum(tier => (long)tier.Score));
        Achievements = Array.AsReadOnly(achievements);
        ById = achievements.ToFrozenDictionary(achievement => achievement.Id);
        Cards = seed.Achievements.Where(tier => tier.CardId != 0).ToFrozenDictionary(tier => tier.CardId);
        Awards = seed.Awards.ToFrozenDictionary(award => award.Level);
    }

    internal IEnumerable<AchievementDefinition> InGroup(byte group)
        => Achievements.Where(achievement => group == 0 || achievement.Group == group || achievement.ParentGroup == group);

    private static ProgressionCatalog Load()
    {
        using var stream = typeof(ProgressionCatalog).Assembly.GetManifestResourceStream("AFServer.ProgressionCatalog")
            ?? throw new InvalidDataException("EXE 缺少角色成长目录。");
        var seed = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Deserialize<Seed>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("角色成长目录为空。");

        return new ProgressionCatalog(seed);
    }

    private sealed record Seed(int SchemaVersion, uint MaxLevel, int MaxExperience, AchievementTier[] Achievements, AchievementAward[] Awards);
}

/// <summary>
/// All levels of one achievement. Single-level achievements retain native Level=0; Flags expresses
/// completion.
/// </summary>
internal sealed class AchievementDefinition(AchievementTier[] tiers)
{
    internal IReadOnlyList<AchievementTier> Tiers { get; } = Array.AsReadOnly(tiers);
    internal AchievementTier Highest => Tiers[^1];
    internal uint Id => Highest.Id;
    internal byte Group => Highest.Group;
    internal byte ParentGroup => Highest.ParentGroup;
    internal ushort CompletedFlags => (ushort)(1 | (Highest.IsLeveled ? 2 : 0) | (Highest.Hidden ? 4 : 0));

    internal void Validate()
    {
        if (Id == 0 || Group is 0 or >= ProgressionCatalog.GroupCount || ParentGroup >= ProgressionCatalog.GroupCount ||
            Tiers.Count > 15 || Tiers.Select(tier => tier.Level).Distinct().Count() != Tiers.Count ||
            Tiers.Any(tier => tier.Group != Group || tier.ParentGroup != ParentGroup ||
                tier.IsLeveled != Highest.IsLeveled || tier.Hidden != Highest.Hidden ||
                (tier.IsLeveled ? tier.Level is < 1 or > 15 : tier.Level != 0)))
            throw new InvalidDataException($"成就 {Id} 的分级或分类无效。");
    }
}

internal sealed record AchievementTier(uint Id, byte Group, byte ParentGroup, byte Level, ulong Goal, uint Score,
    uint CardId, bool IsLeveled, bool Hidden);

internal sealed record AchievementAward(ushort Level, uint RequiredScore, AchievementAwardItem[] Items);

internal sealed record AchievementAwardItem(uint ItemId, uint Hours);
