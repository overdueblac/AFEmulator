namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Recommends a short evasion direction from nearby-enemy snapshots without claiming reachability or
/// changing native position/velocity.
/// </summary>
internal static class AfdevBioBotEvasion
{
    public const float TriggerDistance = 850;
    public const float ReleaseDistance = 1200;

    public static bool IsNearby(AfdevBioBotPoint origin, AfdevBioBotPoint threat, float distance) =>
        origin.IsValid && threat.IsValid && Math.Abs(origin.Z - threat.Z) <= 192 &&
        HorizontalDistanceSquared(origin, threat) <= distance * distance;

    /// <summary>
    /// Prioritizes distance from the nearest enemy while considering lateral space. Every straight segment
    /// must avoid crossing a closer attacker.
    /// </summary>
    public static IReadOnlyList<AfdevBioBotPoint> Recommend(AfdevBioBotPoint origin,
        IReadOnlyList<AfdevBioBotPoint> threats, int preferredSide)
    {
        if (!origin.IsValid || threats.Count == 0 || threats.Any(item => !item.IsValid))
            return [];

        AfdevBioBotPoint nearest = threats.MinBy(item => HorizontalDistanceSquared(origin, item));
        double heading = Math.Atan2(origin.Y - nearest.Y, origin.X - nearest.X);
        int side = preferredSide < 0 ? -1 : 1;
        var candidates = new List<AfdevBioBotPoint>();

        foreach (float length in new[] { 420f, 260f, 140f })
        {
            foreach (double turn in new[] { 0d, side * Math.PI / 4, -side * Math.PI / 4,
                side * Math.PI / 2, -side * Math.PI / 2, side * Math.PI * 3 / 4, -side * Math.PI * 3 / 4, Math.PI })
            {
                var point = new AfdevBioBotPoint(origin.X + (float)Math.Cos(heading + turn) * length,
                    origin.Y + (float)Math.Sin(heading + turn) * length, origin.Z);

                if (IsSafeDirection(origin, point, threats))
                    candidates.Add(point);
            }
        }

        return candidates.OrderByDescending(point => Separation(point, threats)).ToArray();
    }

    /// <summary>
    /// Checks geometric direction only. Native queries handle walls, edges, collision volumes, and walking
    /// reachability.
    /// </summary>
    public static bool IsSafeDirection(AfdevBioBotPoint origin, AfdevBioBotPoint destination,
        IReadOnlyList<AfdevBioBotPoint> threats)
    {
        if (!origin.IsValid || !destination.IsValid || threats.Count == 0 || threats.Any(item => !item.IsValid) ||
            Math.Abs(destination.Z - origin.Z) > 192 || Separation(destination, threats) < Separation(origin, threats) + 8)
            return false;

        double dx = destination.X - origin.X;
        double dy = destination.Y - origin.Y;
        double lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 1)
            return false;

        foreach (AfdevBioBotPoint threat in threats)
        {
            double fraction = Math.Clamp(((threat.X - origin.X) * dx + (threat.Y - origin.Y) * dy) / lengthSquared, 0, 1);
            var closest = new AfdevBioBotPoint(origin.X + (float)(fraction * dx), origin.Y + (float)(fraction * dy), origin.Z);
            double clearance = Math.Min(Math.Sqrt(HorizontalDistanceSquared(origin, threat)), 320);

            if (Math.Sqrt(HorizontalDistanceSquared(closest, threat)) + 12 < clearance)
                return false;
        }

        return true;
    }

    public static double Separation(AfdevBioBotPoint point, IReadOnlyList<AfdevBioBotPoint> threats) =>
        threats.Count == 0 ? double.PositiveInfinity : threats.Min(threat => Math.Sqrt(HorizontalDistanceSquared(point, threat)));

    public static double HorizontalDistanceSquared(AfdevBioBotPoint left, AfdevBioBotPoint right) =>
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2);
}
