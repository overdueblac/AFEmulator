using System.Text.Json.Nodes;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Native PVE revival-coin transactions. Success returns the committed remaining count; negative values
/// reject revival.
/// </summary>
public interface IPveReviveCoinStore
{
    /// <summary>
    /// Reloads the authoritative balance for this round's exact session, refreshing display on all mode
    /// logins and after in-match purchases.
    /// </summary>
    ValueTask<int> GetCountAsync(MatchStartSnapshot snapshot, MatchStartMember member, CancellationToken cancellationToken);

    /// <summary>
    /// Idempotently deducts one coin by frozen round, exact session, and native request sequence. Never
    /// report success to the native world before commit.
    /// </summary>
    ValueTask<int> ConsumeAsync(MatchStartSnapshot snapshot, MatchStartMember member, uint sequence,
        CancellationToken cancellationToken);
}

/// <summary>
/// Revival coins share the player inventory. Count all stacks in storage and on the root; manual equipping
/// is unnecessary.
/// </summary>
internal static class PveReviveCoins
{
    internal const uint ItemId = 100211;
    internal static bool Supports(uint mode) => mode is 0x2001 or 0x2002 or 0x2003 or 0x2005;

    internal static int Count(PlayerState player)
        => (int)Math.Min(int.MaxValue, player.Props.Where(IsCoin)
            .Aggregate(0UL, (total, prop) => Math.Min(int.MaxValue,
                total + Math.Min(int.MaxValue, PlayerState.Number(prop, "stack_num", 1)))));

    internal static int Apply(PlayerState player, Guid allocation, Guid session, uint sequence)
    {
        if (allocation == Guid.Empty || session == Guid.Empty || sequence == 0)
            throw new ArgumentException("复活币扣除必须具有回合、会话和正请求序号。");

        string key = $"{allocation:N}/{session:N}";
        var receipts = player.Document["pve_revive_receipts"] as JsonObject;

        if (receipts?[key] is JsonObject prior && sequence <= PlayerState.Number(prior, "sequence"))
            return sequence == PlayerState.Number(prior, "sequence") ? checked((int)PlayerState.Number(prior, "remaining")) : -1;

        var coin = player.Props.Where(IsCoin).Where(prop => PlayerState.Number(prop, "stack_num", 1) > 0)
            .OrderBy(prop => PlayerState.Number(prop, "gid")).FirstOrDefault();

        if (coin is null) return -1;

        ulong count = PlayerState.Number(coin, "stack_num", 1);

        if (count == 1) player.Inventory.Remove(coin);
        else coin["stack_num"] = count - 1;

        int remaining = Count(player);

        if (receipts is null)
        {
            receipts = new JsonObject();
            player.Document["pve_revive_receipts"] = receipts;
        }
        if (!receipts.ContainsKey(key))
        {
            while (receipts.Count >= 64) receipts.Remove(receipts.First().Key);
        }
        receipts[key] = new JsonObject { ["sequence"] = sequence, ["remaining"] = remaining };

        return remaining;
    }

    private static bool IsCoin(JsonObject prop) => PlayerState.Number(prop, "item_id") == ItemId;
}
