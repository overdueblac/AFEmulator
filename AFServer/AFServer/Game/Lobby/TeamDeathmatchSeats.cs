namespace AFServer.AssaultFire.Game;

/// <summary>
/// The PH team-room protocol has 32 fixed seats: 0–15 map to native team 1, and 16–31 to team 0.
/// </summary>
internal static class TeamDeathmatchSeats
{
    internal static byte Camp(ushort seat) => seat < 16 ? (byte)1 : (byte)0;

    internal static IEnumerable<int> Fighters(int capacity)
        => Enumerable.Range(0, capacity / 2).Concat(Enumerable.Range(16, capacity / 2));

    internal static IEnumerable<int> Observers(int capacity, int observers)
        => Enumerable.Range(capacity / 2, 16 - capacity / 2)
            .Concat(Enumerable.Range(16 + capacity / 2, 16 - capacity / 2)).Take(observers);

    internal static void Validate(int capacity, int observers)
    {
        if (capacity is < 2 or > 24 || capacity % 2 != 0 || observers < 0 || capacity + observers > 32)
            throw new InvalidDataException("团队战必须为 2–24 个偶数参战席，参战与观察席合计不能超过 32。");
    }
}
