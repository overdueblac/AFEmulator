using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// All private-server items are permanent. The signed client-duration field uses -1 for permanence, rather
/// than an artificially large hour count.
/// </summary>
internal static class InventoryLifetime
{
    internal const int Permanent = -1;

    /// <summary>
    /// Migrates old item durations while preserving GIDs, quantities, durability, and equipment
    /// relationships, committed through the player aggregate.
    /// </summary>
    internal static bool EnsurePermanent(PlayerState player)
    {
        var changed = false;

        foreach (var prop in player.Props)
        {
            changed |= SetPermanent(prop, "validity");
            changed |= SetPermanent(prop, "avail_hours");
        }
        return changed;
    }

    private static bool SetPermanent(JsonObject prop, string field)
    {
        if (prop[field]?.ToString() == "-1") return false;

        prop[field] = Permanent;
        return true;
    }
}
