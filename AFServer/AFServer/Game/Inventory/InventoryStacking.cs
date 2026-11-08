using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Distinguishes ownership records from native quantity stacks without changing persisted quantities or
/// equipment relationships.
/// </summary>
internal static class InventoryStacking
{
    // AF2014 DefaultItemLibrary has twelve StackNum > 0 definitions, including quantity items whose stack definition is 1.
    // ConsumeType=0 includes both permanent items and revival coins, so it cannot determine whether to display a quantity unit.
    internal static bool IsStackable(ulong item) => item is 100054 or 100211 or
        100254 or 100255 or 100256 or 100257 or 100258 or 100500 or 100501 or
        100566 or 100567 or 100680;

    /// <summary>
    /// Sends zero for nonquantity items because the client always appends a quantity unit to positive
    /// functional-item StackNumber values.
    /// </summary>
    internal static uint ClientStackNumber(JsonObject prop)
    {
        if (!IsStackable(PlayerState.Number(prop, "item_id"))) return 0;

        return (uint)Math.Min(uint.MaxValue, PlayerState.Number(prop, "stack_num", 1));
    }
}
