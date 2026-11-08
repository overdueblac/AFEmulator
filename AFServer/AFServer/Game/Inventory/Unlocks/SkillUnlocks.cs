namespace AFServer.AssaultFire.Game;

/// <summary>
/// Preserves the old server's PvE skill-ownership policy by completing item IDs without changing existing
/// items, levels, or client skill values. Applies equally to new players, existing players, and explicitly
/// imported MongoDB saves.
/// </summary>
internal static class SkillUnlocks
{
    // DefaultItemLibrary.ini has thirty MainShowType=3 entries; 100205 is the Location=8 extended skill slot.
    private static readonly uint[] ItemIds =
    [
        100043, 100044, 100046, 100047, 100049, 100050, 100052, 100053,
        100185, 100186, 100187, 100188, 100189, 100313, 100314, 100316,
        100317, 100318, 100319, 100517, 100518, 100575, 100576, 100578,
        100580, 100587, 100614, 100616, 100628, 100636, 100205
    ];

    /// <summary>
    /// Issues only missing skill items. Returns whether the save changed so the caller can commit
    /// atomically with other business state.
    /// </summary>
    internal static bool Ensure(PlayerState player)
    {
        var owned = player.Props.Select(prop => PlayerState.Number(prop, "item_id")).ToHashSet();
        var changed = false;

        foreach (var item in ItemIds)
        {
            if (!owned.Add(item)) continue;
            // Skills are not durability equipment; the extended skill slot retains its original shop-item default durability.
            player.Inventory.Add((System.Text.Json.Nodes.JsonNode)PlayerState.NewProp(player.AllocateGid(), item, durability: item == 100205 ? 100U : 0U));
            changed = true;
        }
        return changed;
    }
}
