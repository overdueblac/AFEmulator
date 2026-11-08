namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotEngagement
{
    private CombatTarget[] nearbyThreats = [];
    private bool evading;

    /// <summary>
    /// Ordinary humans prioritize distance as enemies approach; heroes retain their original melee
    /// behavior.
    /// </summary>
    private async Task<AfdevBioBotCombatDecision?> UpdateEvasionAsync(AfdevBioBotCombatSnapshot current,
        CombatTarget? target, bool navigationActive, AfdevBioBotPoint? destination, CancellationToken token)
    {
        var origin = new AfdevBioBotPoint(current.Location[0], current.Location[1], current.Location[2]);
        CombatTarget? nearest = nearbyThreats.MinBy(item => DistanceSquared(current.Location, item.Location));
        bool ordinaryHuman = current.TeamIndex == 0 && !reader.IsA(current.Pawn.Address, "TGBioGame.TGBioHumanHeroPawn");
        bool threatened = ordinaryHuman && nearest != null && AfdevBioBotEvasion.IsNearby(origin,
            new AfdevBioBotPoint(nearest.Location[0], nearest.Location[1], nearest.Location[2]),
            evading ? AfdevBioBotEvasion.ReleaseDistance : AfdevBioBotEvasion.TriggerDistance);

        if (threatened && !evading)
        {
            AfdevBioBotCombatResult sight = await combat.ExecuteAsync(AfdevBioBotCombatAction.LineOfSight,
                current, nearest!.Identity.Address, token).ConfigureAwait(false);

            threatened = sight.NativeExecuted && !sight.Interrupted && sight.BoolResult == true;
        }
        if (evading != threatened)
        {
            evading = threatened;
            revision++;
            save("behavior-evasion", new UpdateEvasionAsyncJson1 { Pawn = current.Pawn, evading = evading, revision = revision, Threat = nearest,
                TriggerDistance = AfdevBioBotEvasion.TriggerDistance, ReleaseDistance = AfdevBioBotEvasion.ReleaseDistance });
        }
        if (!evading)
            return null;

        // Leave native claw range first; reload waits and stationary firing must not continue preventing evasion.
        await CeaseFireAsync().ConfigureAwait(false);

        if (navigationActive)
            await FaceMovementAsync(current, token, destination).ConfigureAwait(false);

        return Decision(current, target ?? nearest, false, "EvadeNearbyEnemy");
    }
}
