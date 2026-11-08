using System.Globalization;
using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Progression within the player aggregate. Achievements, selected calling cards, privacy, and reward
/// claims share the inventory save version.
/// </summary>
internal sealed class PlayerProgression(JsonObject document)
{
    internal uint Level => checked((uint)PlayerState.Number(document, "level"));
    internal int Experience => checked((int)PlayerState.Number(document, "experience"));
    internal uint AchievementScore => checked((uint)PlayerState.Number(document, "achievement_score"));
    internal uint CardId { get => checked((uint)PlayerState.Number(document, "card_id")); set => document["card_id"] = value; }
    internal ushort PrivacyFlags { get => checked((ushort)PlayerState.Number(document, "privacy_flags")); set => document["privacy_flags"] = value; }
    internal uint AwardFlags { get => checked((uint)PlayerState.Number(document, "award_flags")); set => document["award_flags"] = value; }

    internal CompletedAchievement Achievement(uint id)
    {
        var entry = (JsonObject)document["achievements"]![id.ToString(CultureInfo.InvariantCulture)]!;

        return new CompletedAchievement(id, checked((byte)PlayerState.Number(entry, "level")),
            PlayerState.Number(entry, "completed_at"), PlayerState.Number(entry, "cumulate"),
            checked((ushort)PlayerState.Number(entry, "flags")));
    }

    internal bool OwnsCard(AchievementTier tier)
    {
        var completed = Achievement(tier.Id);

        return (completed.Flags & 1) != 0 && completed.Level >= tier.Level;
    }
}

internal sealed record CompletedAchievement(uint Id, byte Level, ulong CompletedAt, ulong Cumulate, ushort Flags);
