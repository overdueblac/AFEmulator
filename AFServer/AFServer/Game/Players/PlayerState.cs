using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// A player's persistent aggregate. Commit wallet, items, and current equipment together so debits and
/// delivery cannot diverge.
/// </summary>
internal sealed class PlayerState(ulong uin, JsonObject document)
{
    internal ulong Uin { get; } = uin;
    internal JsonObject Document { get; } = document;
    internal JsonObject Wallet => (JsonObject)Document["wallet"]!;
    internal JsonArray Inventory => (JsonArray)Document["inventory"]!;
    internal PlayerProgression Progression => new((JsonObject)Document["progression"]!);
    internal IEnumerable<JsonObject> Props => Inventory.OfType<JsonObject>();
    internal string Nickname { get => Document["nickname"]?.GetValue<string>() ?? $"Player{Uin}"; set => Document["nickname"] = value; }
    internal ulong CurrentRole { get => Number(Document, "current_role_gid"); set => Document["current_role_gid"] = value; }
    internal ulong CurrentBag { get => Number(Document, "current_bag_gid"); set => Document["current_bag_gid"] = value; }
    internal long Balance(string currency) => checked((long)Number(Wallet, currency));
    internal void Debit(string currency, long amount) => Wallet[currency] = checked(Balance(currency) - amount);
    internal ulong AllocateGid()
    {
        var next = Number(Document, "next_gid", (Uin << 32) | 100);
        var used = Props.Select(p => Number(p, "gid")).ToHashSet();

        while (used.Contains(next)) next = checked(next + 1);
        Document["next_gid"] = checked(next + 1);
        return next;
    }
    internal static ulong Number(JsonObject value, string key, ulong fallback = 0)
    {
        return value[key] is { } node ? ulong.Parse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture) : fallback;
    }

    internal static JsonObject NewProp(ulong gid, uint item, ulong owner = 0, byte location = 12, uint durability = 100)
        => new() { ["gid"] = gid, ["item_id"] = item, ["owner_gid"] = owner, ["location"] = location,
            ["durability"] = durability, ["durability_max"] = durability, ["stack_num"] = 1,
            ["avail_hours"] = InventoryLifetime.Permanent, ["validity"] = InventoryLifetime.Permanent, ["gain_type"] = 1 };

    internal static PlayerState Create(ulong uin, DateTimeOffset? now = null)
    {
        ulong G(int n) => (uin << 32) | (uint)n;

        var inventory = new JsonArray(
            NewProp(G(1), 100600, 1, 10), NewProp(G(2), 100026, 1, 12, 0),
            NewProp(G(3), 100063, 0, 12, 0), NewProp(G(4), 100497, G(2), 0, 0),
            NewProp(G(5), 100009, G(2), 1), NewProp(G(6), 100058, G(2), 2, 0),
            NewProp(G(7), 100010, G(2), 3, 0), NewProp(G(8), 300121, G(1), 255, 0),
            NewProp(G(9), 300122, G(1), 255, 0), NewProp(G(10), 100602, G(1), 0, 0),
            NewProp(G(11), 100049, 0, 12, 0));
        var player = new PlayerState(uin, new JsonObject { ["version"] = 1, ["nickname"] = $"Player{uin}",
            ["wallet"] = new JsonObject { ["ap"] = 500000, ["gp"] = 100000, ["mp"] = 100000 },
            ["inventory"] = inventory, ["current_role_gid"] = G(1), ["current_bag_gid"] = G(2), ["next_gid"] = G(100) });

        SkillUnlocks.Ensure(player);
        ProgressionUnlocks.Ensure(player, now);
        return player;
    }
}
