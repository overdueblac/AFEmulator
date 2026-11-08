namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Allocator-provided bounded preparation progress. Does not replace native readiness, snapshot checks, or
/// grants.
/// </summary>
public interface IMatchPreparationAllocator
{
    TimeSpan PreparationTimeout { get; }
    (AfdevRuntimeState State, int Attempt, int Maximum) GetPreparation(Guid allocationId);
}
