using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Modes.Bio;

/// <summary>
/// PRI updates for each eligible candidate before selection. Change only PreZombieInfo lev/type, then copy
/// the complete structure to CurZombieInfo, preserving every other field. Never replace that copy with a
/// new empty structure.
/// </summary>
public sealed record BioCandidateSelectionUpdate(int PlayerId, int PriArrayIndex,
    float LastTurnZombieTime, float WaitChangeBioTime)
{
    /// <summary>
    /// Selection clears this flag first. A flag set after conversion returns is outside this result.
    /// </summary>
    public bool GeneralTurnZombie => false;

    /// <summary>Overwrites only existing PreZombieInfo.lev.</summary>
    public int PreviousZombieLevel => -1;

    /// <summary>Overwrites only existing PreZombieInfo.Type.</summary>
    public int PreviousZombieType => -1;

    /// <summary>Copies complete PreZombieInfo to CurZombieInfo after updating those two fields.</summary>
    public bool CopyPreviousZombieInfoToCurrent => true;
}

/// <summary>Purpose of an initial-selection slot, not a wire enum or actual Pawn type.</summary>
public enum BioInitialMutationKind
{
    /// <summary>The Terminator slot allocated first.</summary>
    Terminator,
    /// <summary>The ordinary host-zombie slots allocated afterward.</summary>
    Host
}

/// <summary>
/// A TurnToZombie request for a trusted caller, without destroying, spawning, possessing, or equipping
/// Pawns. Write SpecifiedZombieType before execution. ZombieChange arguments retain original script values,
/// not wire encodings. Includes only explicitly assigned script members; do not infer valid initial values
/// for other ZombieChange members.
/// </summary>
public sealed record BioInitialMutationRequest(int PlayerId, byte SpecifiedZombieType,
    int ZombieLevel, int Data2, bool OpenHostTypeSelection)
{
    /// <summary>Original ZombieChangeType value passed by both 2770 branches.</summary>
    public byte ZombieChangeType => 0;

    /// <summary>Original Data3 value passed by both 2770 branches.</summary>
    public int Data3 => 1;
}

/// <summary>
/// A consumed selection slot. Request is null if Owner cannot cast to a BIO Controller; the slot remains
/// consumed without replacement. A nonnull Request means conversion should be attempted, not successful
/// infection or an actual live-player count.
/// </summary>
public sealed record BioInitialMutationSlot(int PlayerId, int PriArrayIndex,
    BioInitialMutationKind Kind, BioInitialMutationRequest? Request);

/// <summary>
/// GRI write values calculated by 2770 from consumed slots, not successfully spawned Pawns. The script
/// writes counts only after all ordered conversion calls return; never publish them when requests are
/// merely queued.
/// </summary>
public sealed record BioInitialMutationScriptCounts(int TotalPlayers, int Zombies, int Humans);

/// <summary>
/// Immutable selection results for a trusted caller within the same session/round boundary. Candidate
/// initialization and slot requests preserve script order. Performs no TurnToZombie,
/// OpenSelectHostZombieType, ComputerGridNum, or replication. Subsequent bGeneralTurnZombie/wTimesHuman
/// writes wait for actual conversion return. Original flag writes do not require successful Pawn creation,
/// and this result makes no conversion-success claim.
/// </summary>
public sealed record BioInitialMutationSelection(ImmutableArray<BioCandidateSelectionUpdate> CandidateUpdates,
    ImmutableArray<int> OrderedCandidatePlayerIds, ImmutableArray<BioInitialMutationSlot> Slots,
    int DesiredHostCount, int DesiredTerminatorCount, BioInitialMutationScriptCounts? ScriptCounts)
{
    /// <summary>
    /// When candidates are insufficient, the original script calls NotifyCanTurnToHumanHero and returns
    /// false without publishing GRI counts.
    /// </summary>
    public bool ShouldNotifyCanTurnToHumanHero => ScriptCounts is null;
}
