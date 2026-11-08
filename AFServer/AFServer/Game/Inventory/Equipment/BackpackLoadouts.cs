using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Maintains root attachments for five native backpacks and the basic weapons needed for first entry
/// without resetting existing loadouts.
/// </summary>
internal static class BackpackLoadouts
{
    // DefaultItemLibrary defines Package, Bag2, Bag3, Bag4, and Bag5. The last three are also backpack roots, not ordinary items inside a backpack.
    private static readonly uint[] ItemIds = [100026, 100063, 100035, 100036, 100037];

    internal static bool IsBackpack(ulong item) => item is 100026 or 100063 or 100035 or 100036 or 100037;

    /// <summary>
    /// Repairs old attachments and fills empty backpacks on login. The caller commits these changes with
    /// other player state; repeated logins issue no duplicates.
    /// </summary>
    internal static bool Ensure(PlayerState player, JsonObject catalog)
    {
        var bags = new List<JsonObject>(ItemIds.Length);
        var changed = false;

        foreach (var item in ItemIds)
        {
            var bag = player.Props.FirstOrDefault(prop => PlayerState.Number(prop, "item_id") == item);

            if (bag is null)
            {
                bag = PlayerState.NewProp(player.AllocateGid(), item, durability: 0);
                player.Inventory.Add((System.Text.Json.Nodes.JsonNode)bag);
                changed = true;
            }
            bags.Add(bag);
        }

        var allBags = player.Props.Where(prop => IsBackpack(PlayerState.Number(prop, "item_id"))).ToArray();
        var selected = allBags.FirstOrDefault(bag => PlayerState.Number(bag, "gid") == player.CurrentBag)
            ?? allBags.FirstOrDefault(bag => PlayerState.Number(bag, "owner_gid") == 1) ?? bags[0];
        var selectedGid = PlayerState.Number(selected, "gid");
        var weaponItems = catalog["weapon_commodities"]!.AsArray()
            .Select(value => ulong.Parse(catalog["shop_item_map"]![value!.ToString()]!.ToString(),
                System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

        if (player.CurrentBag != selectedGid)
        {
            player.CurrentBag = selectedGid;
            changed = true;
        }

        foreach (var bag in allBags)
        {
            var gid = PlayerState.Number(bag, "gid");
            var owner = gid == selectedGid ? 1UL : 0UL;

            // Old logic attached backpacks 3–5 under the current backpack. Repair their parent without moving their weapons or skills.
            if (PlayerState.Number(bag, "owner_gid") != owner || PlayerState.Number(bag, "location") != 12)
            {
                bag["owner_gid"] = owner;
                bag["location"] = 12;
                changed = true;
            }

            var hasWeapon = player.Props.Any(prop => PlayerState.Number(prop, "owner_gid") == gid
                && PlayerState.Number(prop, "location") <= 5
                && weaponItems.Contains(PlayerState.Number(prop, "item_id")));

            // Native RecoverSyncArrayInfo reconstructs bInit from weapon and skill lists; completely empty backpacks do not appear in the match UI.
            // Give each empty backpack its own basic MK4, avoiding borrowed GIDs and preserving the player's chosen loadout.
            if (!hasWeapon)
            {
                player.Inventory.Add((System.Text.Json.Nodes.JsonNode)PlayerState.NewProp(player.AllocateGid(), 100058, gid, 2, 0));
                changed = true;
            }
        }
        return changed;
    }
}
