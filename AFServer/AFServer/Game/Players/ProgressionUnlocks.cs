using System.Globalization;
using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// The default maximum-level, all-achievements, all-cards policy uses one idempotent completion path for
/// new saves, existing saves, and explicit imports.
/// </summary>
internal static class ProgressionUnlocks
{
    /// <summary>
    /// Completes progression only, preserving wallets, equipment, selected cards, and reward-claim flags
    /// for the caller's shared commit.
    /// </summary>
    internal static bool Ensure(PlayerState player, DateTimeOffset? now = null)
    {
        var catalog = ProgressionCatalog.Instance;
        var changed = false;
        var state = GetObject(player.Document, "progression", ref changed);
        var achievements = GetObject(state, "achievements", ref changed);
        // AF2014 C002/C009 native callbacks call _localtime64_s; the network Date field uses Unix seconds.
        var completedAt = checked((ulong)(now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());

        changed |= SetNumber(state, "level", catalog.MaximumLevel);
        changed |= SetNumber(state, "experience", checked((ulong)catalog.MaximumExperience));
        changed |= SetNumber(state, "achievement_score", catalog.TotalScore);
        foreach (var definition in catalog.Achievements)
        {
            var entry = GetObject(achievements, definition.Id.ToString(CultureInfo.InvariantCulture), ref changed);

            changed |= SetNumber(entry, "level", definition.Highest.Level);
            changed |= SetNumber(entry, "cumulate", definition.Highest.Goal);
            // Leave NeedNotice unset to avoid hundreds of achievement-unlock prompts on login.
            changed |= SetNumber(entry, "flags", definition.CompletedFlags);
            if (PlayerState.Number(entry, "completed_at") == 0) changed |= SetNumber(entry, "completed_at", completedAt);
        }

        var cardId = PlayerState.Number(state, "card_id", ProgressionCatalog.DefaultCardId);

        if (cardId != ProgressionCatalog.DefaultCardId && (cardId > uint.MaxValue || !catalog.Cards.ContainsKey((uint)cardId)))
            cardId = ProgressionCatalog.DefaultCardId;
        changed |= SetNumber(state, "card_id", cardId);
        if (state["privacy_flags"] is null) changed |= SetNumber(state, "privacy_flags", 0);
        if (state["award_flags"] is null) changed |= SetNumber(state, "award_flags", 0);
        return changed;
    }

    private static JsonObject GetObject(JsonObject parent, string key, ref bool changed)
    {
        if (parent[key] is JsonObject existing) return existing;
        if (parent[key] is not null) throw new InvalidDataException($"玩家成长字段 {key} 不是对象。");

        var value = new JsonObject();

        parent[key] = value;
        changed = true;
        return value;
    }

    private static bool SetNumber(JsonObject parent, string key, ulong value)
    {
        if (parent[key] is not null && PlayerState.Number(parent, key) == value) return false;
        parent[key] = value;
        return true;
    }
}
