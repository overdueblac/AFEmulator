using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Settlement receipts belong to the player aggregate. Commit the credit and receipt together and reload
/// the save after failure.
/// </summary>
internal static class MatchSettlementReceipts
{
    private const string ReceiptsKey = "match_settlements";
    private const int MaximumReceipts = 128;

    internal static uint Reward(PlayerState player, Guid allocationId) =>
        player.Document[ReceiptsKey]?[allocationId.ToString("N")]?["game_points"]?.GetValue<uint>() ?? 0;

    internal static bool Apply(PlayerState player, MatchSettlementSnapshot snapshot, MatchSettlementPlayer participant)
    {
        if (!snapshot.Players.Contains(participant) || participant.Member.Profile.Uin != player.Uin ||
            participant.Member.Session.Uin != player.Uin || participant.Member.Observer ||
            !snapshot.Start.Members.Any(member => ReferenceEquals(member, participant.Member)) ||
            snapshot.Start.AllocationId == Guid.Empty || snapshot.AttemptId == Guid.Empty ||
            (!snapshot.Completed && participant.GamePoints != 0))
            throw new InvalidDataException("奖励来源与冻结参战玩家、原生结束状态不一致。");

        var receipts = player.Document[ReceiptsKey] as JsonObject;
        string key = snapshot.Start.AllocationId.ToString("N");

        if (receipts?[key] is JsonObject prior)
        {
            if (prior["attempt"]?.GetValue<string>() != snapshot.AttemptId.ToString("N") ||
                prior["session"]?.GetValue<string>() != participant.Member.Session.SessionId.ToString("N") ||
                prior["world_generation"]?.GetValue<long>() != snapshot.WorldGeneration)
                throw new InvalidDataException("已保存的结算收据属于不同启动尝试或精确会话。");

            // The first departure fixes the player's result. An early departure with zero reward cannot later become full participation at match end.
            return false;
        }

        long balance = checked(player.Balance("gp") + participant.GamePoints);

        if (balance > uint.MaxValue)
            throw new InvalidOperationException("结算后金币余额超过客户端协议范围。");

        if (receipts is null)
        {
            receipts = new JsonObject();
            player.Document[ReceiptsKey] = receipts;
        }

        player.Wallet["gp"] = balance;
        receipts[key] = new JsonObject
        {
            ["attempt"] = snapshot.AttemptId.ToString("N"),
            ["session"] = participant.Member.Session.SessionId.ToString("N"),
            ["world_generation"] = snapshot.WorldGeneration,
            ["completed"] = snapshot.Completed,
            ["observed_at"] = snapshot.ObservedAtUtc.ToUnixTimeMilliseconds(),
            ["game_points"] = participant.GamePoints
        };

        // Accept only the current live allocation; historical allocations cannot reenter settlement. Bounded receipts are not a second ledger.
        while (receipts.Count > MaximumReceipts)
        {
            string oldest = receipts.Where(entry => entry.Key != key)
                .OrderBy(entry => entry.Value?["observed_at"]?.GetValue<long>() ?? 0).First().Key;

            receipts.Remove(oldest);
        }

        return true;
    }
}
