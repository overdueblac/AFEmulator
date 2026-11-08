using System.Collections.Immutable;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Read-only settlement for one native world. Members retain their frozen start identities, and statistics
/// are already encoded in the mode-specific TDR layout.
/// </summary>
/// <remarks>
/// Completed means the whole match ended. Early departures retain their last statistics, with GamePoints
/// remaining zero.
/// </remarks>
public sealed record MatchSettlementSnapshot(MatchStartSnapshot Start, Guid AttemptId, long WorldGeneration,
    bool Completed, DateTimeOffset ObservedAtUtc, ImmutableArray<MatchSettlementPlayer> Players,
    ImmutableArray<MatchSettlementSpectator> Spectators, ImmutableArray<byte> Statistics);

/// <summary>
/// Actual participants and native-calculated base coins. Contains no wallet copy and never turns bots into
/// player accounts.
/// </summary>
public sealed record MatchSettlementPlayer(MatchStartMember Member, byte Camp, uint GamePoints);

/// <summary>Observers actually seen in the native world, excluded from match rewards.</summary>
public sealed record MatchSettlementSpectator(MatchStartMember Member, byte Camp);
