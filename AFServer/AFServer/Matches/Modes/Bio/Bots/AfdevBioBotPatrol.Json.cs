using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotPatrol
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ChooseChasePlanAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint From { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint To { get; init; }
        public required double Gap { get; init; }
    }

    private sealed record ChooseChasePlanAsyncJson2
    {
        public required uint TargetPawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint From { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint To { get; init; }
        public required float[] Location { get; init; }
        public required string Reason { get; init; }
    }

    private sealed record DirectlyReachableAsyncJson1
    {
        public required long directQueries { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required float[] Location { get; init; }
        public required uint goal { get; init; }
        public required float[] goalLocation { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteResult result { get; init; }
    }

    private sealed record RecordChaseResultJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint From { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.PatrolPoint To { get; init; }
        public required float[] Location { get; init; }
        public required global::System.Guid RouteId { get; init; }
        public required string Limitation { get; init; }
    }

    private sealed record RecordChaseResultJson2
    {
        public required uint TargetPawn { get; init; }
        public required long routes { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.NavigationPlan plan { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteOutcome Outcome { get; init; }
        public required float[] Location { get; init; }
        public required float[]? After { get; init; }
        public required double RemainingStraightDistance { get; init; }
        public required double chaseBestDistance { get; init; }
        public required bool progressed { get; init; }
        public required global::System.DateTimeOffset chaseProgressAt { get; init; }
        public required int chaseFailures { get; init; }
        public required int ConfirmedBridges { get; init; }
        public required long directQueries { get; init; }
        public required bool GoalIsEnemy { get; init; }
    }

    private sealed record ChooseLearnedPlanAsyncJson1
    {
        public required int Revision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint origin { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkCandidate candidate { get; init; }
        public required bool NativeReachabilityPending { get; init; }
    }

    private sealed record ChooseLearnedPlanAsyncJson2
    {
        public required int Revision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required float Radius { get; init; }
        public required float Height { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint origin { get; init; }
        public required uint TargetPawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkCandidate candidate { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteOutcome Outcome { get; init; }
    }

    private sealed record RecordLearnedResultJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? Point { get; init; }
        public required int LearnedNode { get; init; }
        public required double LearnedCost { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkStep? WalkStep { get; init; }
        public required float Radius { get; init; }
        public required float Height { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteOutcome Outcome { get; init; }
        public required global::System.Guid RouteId { get; init; }
        public required bool UsedObservedStep { get; init; }
        public required bool NativeReached { get; init; }
        public required bool progressed { get; init; }
        public required double? BestCost { get; init; }
    }

    private sealed record RecordLearnedResultJson2
    {
        public required long Actions { get; init; }
        public required global::System.Collections.Generic.Dictionary<global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteOutcome, long> Outcomes { get; init; }
        public required long ObservedTrials { get; init; }
        public required long ObservedReached { get; init; }
    }

    private sealed record RunAsyncJson1
    {
        public required uint Controller { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required float[] Location { get; init; }
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
    }

    private sealed record RunAsyncJson2
    {
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision? decision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required float[] Location { get; init; }
        public required string Reason { get; init; }
    }

    private sealed record RunAsyncJson3
    {
        public required long routes { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision? decision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required float[] Location { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.NavigationPlan plan { get; init; }
    }

    private sealed record RunAsyncJson4
    {
        public required long routes { get; init; }
        public required int completedSegments { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.NavigationPlan plan { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision? decision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteResult result { get; init; }
    }

    private sealed record RunAsyncJson5
    {
        public required long routes { get; init; }
        public required int completedSegments { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationState retired { get; init; }
    }

    private sealed record RunRouteAsyncJson1
    {
        public required long routes { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.NavigationPlan plan { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision initial { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision current { get; init; }
        public required string Reason { get; init; }
    }

    private sealed record ReadPointsJson1
    {
        public required uint World { get; init; }
        public required int NavigationPoints { get; init; }
    }

    private sealed record RememberNoPathJson1
    {
        public required uint Anchor { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Goal { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required float[] Location { get; init; }
    }

    private sealed record ChooseEvasionPlanAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint origin { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint point { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteOutcome Outcome { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint> ThreatLocations { get; init; }
    }

    private sealed record RecordEvasionResultJson1
    {
        public required long evasionAttempts { get; init; }
        public required long evasionMoved { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint Before { get; init; }
        public required float[]? After { get; init; }
        public required bool moved { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint> ThreatLocations { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.NavigationPlan plan { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteResult result { get; init; }
        public required string Limitation { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotPatrolJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ChooseChasePlanAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ChooseChasePlanAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ChooseEvasionPlanAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ChooseLearnedPlanAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ChooseLearnedPlanAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.DirectlyReachableAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.ReadPointsJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RecordChaseResultJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RecordChaseResultJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RecordEvasionResultJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RecordLearnedResultJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RecordLearnedResultJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RememberNoPathJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunAsyncJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunAsyncJson4))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunAsyncJson5))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.RunRouteAsyncJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotPatrolJsonContext : JsonSerializerContext;
}
