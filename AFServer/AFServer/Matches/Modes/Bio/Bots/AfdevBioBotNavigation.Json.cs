using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotNavigation
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record StartAsyncJson1
    {
        public required global::System.Guid Id { get; init; }
        public required long PawnGeneration { get; init; }
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Controller { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required uint goal { get; init; }
        public required uint frame { get; init; }
        public required uint code { get; init; }
        public required string ProgramHex { get; init; }
        public required string ProgramSha256 { get; init; }
        public required string EvidenceId { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationMode mode { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? point { get; init; }
        public required uint StopOffset { get; init; }
        public required uint LatentIndex { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.QueryObject> Identities { get; init; }
        public required bool DiagnosticOnly { get; init; }
        public required uint MoveMask { get; init; }
        public required uint MoveWord { get; init; }
        public required int ProgramSlotCount { get; init; }
        public required long PublishedPrograms { get; init; }
        public required uint RoundCount { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotNavigationJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionObservation))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigation.StartAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationRetirement))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationState))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotNavigationJsonContext : JsonSerializerContext;
}
