using System.Collections.Frozen;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Equips functional items on the player root using independent item-table locations, without consuming
/// backpack weapon slots.
/// </summary>
internal static class FunctionalEquipment
{
    // AF2014 DefaultItemLibrary contains 121 ResBindType=5 entries.
    // GfxMovie_AvatarRoom.OnEquipItem requests TargetGID=1 for these items; item-table Location=-1 is encoded as 255.
    // Do not infer identity from shop categories or slot numbers: helmet/body-armor slots 3/4 overlap backpack grenade slots.
    private static readonly FrozenDictionary<uint, byte> Locations = new (byte Location, uint[] Items)[]
    {
        (255, [100054, 100145, 100146, 100147, 100148, 100149, 100150, 100181, 100182, 100196,
            100210, 100254, 100255, 100256, 100257, 100258, 100359, 100360, 100361, 100500,
            100501, 100566, 100567, 100583, 100584, 100585, 100612, 100632, 100633, 100634,
            100635, 100644, 100680]),
        (3, [100032]),
        (4, [100033]),
        (5, [100038, 100132]),
        (6, [100039]),
        (7, [100042]),
        (8, [100205]),
        (9, [100154, 100155, 100156, 100157, 100158, 100159, 100160, 100161, 100162, 100163,
            100164, 100165, 100166, 100167, 100168, 100169, 100170, 100193, 100194, 100195,
            100299, 100332, 100349, 100350, 100351, 100352, 100353, 100354, 100355, 100356,
            100357, 100358, 100399, 100400, 100401, 100402, 100403, 100404, 100405, 100406,
            100407, 100408, 100409, 100410, 100411, 100412, 100413, 100414, 100415, 100416,
            100417, 100418, 100573, 100574, 100630, 100631]),
        (13, [100059]),
        (14, [100060]),
        (15, [100183]),
        (17, [100197]),
        (18, [100143]),
        (19, [100211]),
        (20, [100245]),
        (21, [100246]),
        (22, [100290]),
        (23, [100310]),
        (24, [100311]),
        (25, [100242]),
        (26, [100243]),
        (27, [100244]),
        (28, [100240]),
        (29, [100461]),
        (30, [100241]),
        (31, [100531]),
        (32, [100532]),
        (33, [100533]),
        (34, [100537]),
        (35, [100568]),
        (36, [100591]),
        (37, [100592]),
        (39, [100629])
    }.SelectMany(group => group.Items.Select(item => KeyValuePair.Create(item, group.Location))).ToFrozenDictionary();

    internal static bool TryGetLocation(uint item, out byte location) => Locations.TryGetValue(item, out location);

    /// <summary>
    /// Migrates equipped functional items on login for atomic persistence with the player document. Does
    /// not equip stored items on the player's behalf.
    /// </summary>
    internal static bool Ensure(PlayerState player)
    {
        var equipped = player.Props.Where(prop => PlayerState.Number(prop, "owner_gid") != 0
                && Locations.ContainsKey((uint)PlayerState.Number(prop, "item_id")))
            .OrderByDescending(prop => PlayerState.Number(prop, "owner_gid") == 1)
            .ThenByDescending(prop => PlayerState.Number(prop, "owner_gid") == player.CurrentBag)
            .ThenBy(prop => PlayerState.Number(prop, "gid")).ToArray();
        var occupied = new HashSet<byte>();
        var changed = false;

        foreach (var prop in equipped)
        {
            var location = Locations[(uint)PlayerState.Number(prop, "item_id")];
            // Old saves have no equip timestamp. Prefer existing root attachments, then the current backpack, then GID for a stable same-slot result.
            // Fixed slots such as sprays remain exclusive; slot 255 follows native EquipWithProp semantics and preserves other items at that location.
            var owner = location == byte.MaxValue || occupied.Add(location) ? 1UL : 0UL;

            if (owner == 0) location = 12;
            if (PlayerState.Number(prop, "owner_gid") == owner && PlayerState.Number(prop, "location") == location) continue;

            prop["owner_gid"] = owner;
            prop["location"] = location;
            changed = true;
        }
        return changed;
    }
}
