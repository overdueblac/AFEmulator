namespace AFServer.AssaultFire.Game;

/// <summary>Head, face, and waist accessories with client-product nSaleSubType values 6/7/8.</summary>
internal static class AvatarAccessories
{
    // Source: actual product classifications in the current client's DefaultCommodityLibrary.ini.
    // Accessories and weapons share slot values 0/1/2. Classify accessories by product identity and attach them to the character, not by Location alone.
    private static readonly HashSet<uint> ItemIds =
    [
        100017, 100018, 100019, 100024, 100025, 100031, 100081, 100082, 100083, 100084,
        100085, 100086, 100087, 100088, 100089, 100090, 100091, 100092, 100093, 100094,
        100095, 100096, 100097, 100098, 100101, 100102, 100103, 100104, 100116, 100117,
        100118, 100119, 100120, 100121, 100124, 100125, 100126, 100127, 100128, 100129,
        100200, 100201, 100202, 100203, 100223, 100224, 100230, 100264, 100265, 100266,
        100267, 100268, 100269, 100272, 100275, 100276, 100277, 100278, 100279, 100303,
        100304, 100305, 100320, 100321, 100341, 100347, 100369, 100370, 100371, 100372,
        100373, 100377, 100425, 100428, 100432, 100450, 100451, 100452, 100455, 100527,
        100528, 100529, 100549, 100550, 100551, 100588, 100589, 100590, 100606, 100607,
        100608, 100609, 100610, 100611
    ];

    internal static bool Contains(uint item) => ItemIds.Contains(item);
}
