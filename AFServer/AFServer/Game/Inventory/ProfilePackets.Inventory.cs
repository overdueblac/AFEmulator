using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

// Preserve the inventory protocol's login grouping, stack merging, and original order of five entries per packet.
internal static partial class ProfilePackets
{
    internal static IEnumerable<byte[]> Inventory(PlayerState player, bool login = false)
    {
        var props = MergeStacks(player.Props).ToArray();

        if (props.Length == 0)
        {
            yield return GameWire.Packet(0xA006, new TdrWriter().U16(GameWire.ZoneSuccess).U8(1).U16(0));
            yield break;
        }

        var groups = new List<JsonObject[]>();
        var known = new HashSet<ulong>();

        if (login)
        {
            // Publish character/backpack roots before appearance components and remaining weapons, following the verified legacy-client initialization dependency order.
            foreach (var suffixes in new int[][] { [1, 2, 3, 4], [8, 9, 10], [5, 6, 7] })
            {
                var group = suffixes.Select(s => (player.Uin << 32) | (uint)s)
                    .Select(gid => props.FirstOrDefault(p => PlayerState.Number(p, "gid") == gid)).OfType<JsonObject>().ToArray();

                if (group.Length != 0) groups.Add(group);
                foreach (var prop in group) known.Add(PlayerState.Number(prop, "gid"));
            }
        }
        groups.AddRange(props.Where(p => !known.Contains(PlayerState.Number(p, "gid"))).Chunk(5));
        // Client metadata declares PropInfo as a five-element array; excess entries in one packet corrupt the decode target.
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var body = new TdrWriter().U16(GameWire.ZoneSuccess).U8(index == groups.Count - 1 ? 1 : 0).U16(group.Length);

            foreach (var prop in group) WriteProp(body, player.Uin, prop);
            yield return GameWire.Packet(0xA006, body);
        }
    }

    private static IEnumerable<JsonObject> MergeStacks(IEnumerable<JsonObject> props)
    {
        foreach (var group in props.GroupBy(p => (PlayerState.Number(p, "item_id"), PlayerState.Number(p, "location", 12), PlayerState.Number(p, "owner_gid"))))
        {
            var merged = (JsonObject)group.First().DeepClone();
            var total = group.Aggregate(0UL, (sum, prop) => Math.Min(uint.MaxValue,
                sum + InventoryStacking.ClientStackNumber(prop)));

            merged["stack_num"] = total;
            merged["durability"] = group.Max(p => PlayerState.Number(p, "durability", 100));
            merged["durability_max"] = group.Max(p => PlayerState.Number(p, "durability_max", 100));
            yield return merged;
        }
    }

    internal static void WriteProp(TdrWriter writer, ulong uin, JsonObject prop)
    {
        ulong N(string name, ulong fallback = 0) => PlayerState.Number(prop, name, fallback);
        writer.U64(uin).U64(N("gid")).U32((long)N("item_id")).U64(0).U32(InventoryStacking.ClientStackNumber(prop))
            .U64(0).U32(InventoryLifetime.Permanent).U32((long)N("durability", 100))
            .U32((long)N("durability_max", 100)).U64(N("owner_gid")).U8((int)N("location", 12))
            .U32(InventoryLifetime.Permanent).U32(0).U8((int)N("gain_type"));
    }
}
