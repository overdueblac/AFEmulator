using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Modes.Bio;

/// <summary>
/// Candidate, priority, and conversion-request rules for ordinary BIO initial mutation, based on TGBioMatch
/// 2682, 2770, and 2736. Uses trusted PRI snapshots only; team-deathmatch kills, static class-array order,
/// and client identity do not determine mutation rules.
/// </summary>
public static class BioInitialMutationSelector
{
    /// <summary>Implementation bound on input collections, not the native room-size rule.</summary>
    public const int MaximumPriEntries = 128;

    // TGBioType.EZombieType export400 and byte constants from 2770/2736 jointly confirm these values; this is not a wire-byte codec.
    private const byte HulkZombieType = 0;
    private const byte TerminatorZombieType = 4;

    /// <summary>
    /// Computes candidate updates and requests in Terminator-then-host order without mutating inputs or
    /// invoking external objects. Excludes null PRI, missing Team, nongame players, and observers. Missing
    /// Owner prevents a request only after allocation, leaving candidate count unchanged.
    /// </summary>
    /// <param name="priArray">
    /// Unique trusted player snapshots in actual PRIArray order; their enumerator is never invoked.
    /// </param>
    /// <param name="worldTimeSeconds">Actual WorldInfo.TimeSeconds, not client time or wall-clock time.</param>
    /// <exception cref="ArgumentException">
    /// Identity is duplicate/invalid, the collection exceeds limits, or consumed time is not a finite
    /// single-precision value.
    /// </exception>
    public static BioInitialMutationSelection Select(IReadOnlyList<BioPlayerSelectionInput?> priArray,
        float worldTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(priArray);

        int count = priArray.Count;

        if (count is < 0 or > MaximumPriEntries)
            throw new ArgumentException("PRI 集合数量超过初始变异选人的实现范围。", nameof(priArray));
        if (!float.IsFinite(worldTimeSeconds))
            throw new ArgumentException("世界时间必须为有限单精度值。", nameof(worldTimeSeconds));

        var candidates = new List<Candidate>(count);
        var updates = ImmutableArray.CreateBuilder<BioCandidateSelectionUpdate>();
        HashSet<int> playerIds = [];

        for (int index = 0; index < count; index++)
        {
            BioPlayerSelectionInput? player = priArray[index];

            if (player is null) continue;
            if (player.PlayerId <= 0 || !playerIds.Add(player.PlayerId))
                throw new ArgumentException("PRI 玩家身份必须为正且唯一。", nameof(priArray));
            if (!player.HasTeam || !player.IsGamePlayer || player.OnlySpectator) continue;
            if (!float.IsFinite(player.LastTurnZombieTime))
                throw new ArgumentException("候选的上次变异时间必须为有限单精度值。", nameof(priArray));

            float lastTurnTime = player.LastTurnZombieTime == 0 ? worldTimeSeconds : player.LastTurnZombieTime;
            float waitTime = worldTimeSeconds - lastTurnTime;

            if (!float.IsFinite(waitTime))
                throw new ArgumentException("候选等待时间超出有限单精度范围。", nameof(priArray));

            updates.Add(new(player.PlayerId, index, lastTurnTime, waitTime));
            candidates.Add(new(player, index, waitTime));
        }

        // The immediate swaps in 2770 storage485–678 are not a stable sort. Later higher-priority entries can indirectly reorder equal entries.
        for (int first = 0; first < candidates.Count - 1; first++)
        for (int next = first + 1; next < candidates.Count; next++)
        {
            if (candidates[next].WaitTime > candidates[first].WaitTime)
                (candidates[first], candidates[next]) = (candidates[next], candidates[first]);
        }

        int desiredHosts = candidates.Count < 7 ? 0 : candidates.Count < 13 ? 1 : 2;
        const int desiredTerminators = 1;
        int desiredTotal = desiredHosts + desiredTerminators;
        ImmutableArray<int> orderedPlayers = candidates.Select(candidate => candidate.Player.PlayerId).ToImmutableArray();

        // 2682 requires one Terminator even with zero players. If total demand exceeds candidates, 2770 skips all allocation/GRI-count branches.
        if (desiredTotal > candidates.Count)
            return new(updates.ToImmutable(), orderedPlayers, [], desiredHosts, desiredTerminators, null);

        var slots = ImmutableArray.CreateBuilder<BioInitialMutationSlot>(desiredTotal);

        for (int index = 0; index < desiredTotal; index++)
        {
            Candidate candidate = candidates[index];
            BioInitialMutationKind kind = index < desiredTerminators
                ? BioInitialMutationKind.Terminator : BioInitialMutationKind.Host;
            BioInitialMutationRequest? request = null;

            if (candidate.Player.HasBioController)
            {
                byte specifiedType = kind == BioInitialMutationKind.Terminator ? TerminatorZombieType
                    : candidate.Player.CurrentSpecifyZombieType == TerminatorZombieType ? HulkZombieType
                    : candidate.Player.CurrentSpecifyZombieType;

                request = new(candidate.Player.PlayerId, specifiedType,
                    kind == BioInitialMutationKind.Terminator ? 0 : 1, specifiedType,
                    kind == BioInitialMutationKind.Host);
            }

            slots.Add(new(candidate.Player.PlayerId, candidate.PriArrayIndex, kind, request));
        }

        var scriptCounts = new BioInitialMutationScriptCounts(candidates.Count, slots.Count, candidates.Count - slots.Count);

        return new(updates.ToImmutable(), orderedPlayers, slots.MoveToImmutable(), desiredHosts,
            desiredTerminators, scriptCounts);
    }

    private sealed record Candidate(BioPlayerSelectionInput Player, int PriArrayIndex, float WaitTime);
}
