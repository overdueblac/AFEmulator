using System.Globalization;
using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Completes private-server content from the product catalog without resetting existing items, wallets, or
/// equipment relationships.
/// </summary>
internal static class InventoryUnlocks
{
    /// <summary>
    /// Handles new accounts, existing accounts, and imported saves. Returns whether items were issued or
    /// durations migrated to permanence.
    /// </summary>
    internal static bool Ensure(PlayerState player, JsonObject catalog)
    {
        var owned = player.Props.GroupBy(p => PlayerState.Number(p, "item_id"))
            .ToDictionary(group => group.Key, group => group.First());
        var disabled = catalog["disabled_commodities"]?.AsArray().Select(value => value!.ToString()).ToHashSet() ?? [];
        var changed = InventoryLifetime.EnsurePermanent(player);

        foreach (var (commodity, value) in catalog["shop_item_map"]!.AsObject())
        {
            if (disabled.Contains(commodity)) continue;

            var item = uint.Parse(value!.ToString(), CultureInfo.InvariantCulture);

            // Badge products map to 2xxxxx display IDs, not actual inventory items.
            if (item is not (>= 100000 and < 200000) and not (>= 300000 and < 400000)) continue;

            var bundle = catalog["commodity_bundles"]?[commodity]?.AsArray();
            var items = bundle is { Count: > 0 }
                ? bundle.Select(node => uint.Parse(node!.ToString(), CultureInfo.InvariantCulture)).ToArray()
                : [item];
            var rootItem = items[0];

            if (!owned.TryGetValue(rootItem, out var root))
            {
                root = PlayerState.NewProp(player.AllocateGid(), rootItem, 0, 12, 100);
                player.Inventory.Add((System.Text.Json.Nodes.JsonNode)root);
                owned.Add(rootItem, root);
                changed = true;
            }
            // Some old saves contain only a character-root item. Fill missing components even when the root already exists.
            foreach (var child in items.Skip(1))
            {
                if (owned.ContainsKey(child)) continue;

                var prop = PlayerState.NewProp(player.AllocateGid(), child, PlayerState.Number(root, "gid"), 255, 0);

                player.Inventory.Add((System.Text.Json.Nodes.JsonNode)prop);
                owned.Add(child, prop);
                changed = true;
            }
        }
        return changed;
    }

}
