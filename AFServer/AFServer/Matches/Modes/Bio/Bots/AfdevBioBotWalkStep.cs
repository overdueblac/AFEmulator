namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// A small step proposed from actual adjacent demonstration points, bound to current Pawn identity. After
/// reachability rejection, bounded trial walking is allowed only near the edge start. It neither validates
/// the edge nor changes native collision, displacement, or arrival conditions.
/// </summary>
internal sealed record AfdevBioBotWalkStep(AfdevBioBotPoint From, AfdevBioBotPoint To, QueryObject Pawn)
{
    internal static bool CanStartAt(AfdevBioBotPoint from, AfdevBioBotPoint to, AfdevBioBotPoint origin) =>
        from.IsValid && to.IsValid && origin.IsValid &&
        Math.Abs(from.Z - origin.Z) <= 48 && Math.Abs(to.Z - from.Z) <= 96 &&
        AfdevBioBotWalkRoutes.DistanceSquared(from, origin) <= 80 * 80 &&
        AfdevBioBotWalkRoutes.DistanceSquared(from, to) <= 200 * 200 &&
        AfdevBioBotWalkRoutes.DistanceSquared(origin, to) <= 160 * 160;
}
