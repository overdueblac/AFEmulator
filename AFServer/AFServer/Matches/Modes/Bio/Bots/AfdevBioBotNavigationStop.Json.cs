using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotNavigationStop
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record PrepareAsyncJson1
    {
        public required uint pc { get; init; }
        public required uint frame { get; init; }
        public required uint CurrentFrame { get; init; }
        public required uint Object { get; init; }
        public required byte Debug { get; init; }
        public required byte Padding { get; init; }
        public required uint StackData { get; init; }
        public required uint StackCount { get; init; }
        public required uint StackCapacity { get; init; }
        public required uint Debugger { get; init; }
        public required string FrameHex { get; init; }
    }

    private sealed record PrepareAsyncJson2
    {
        public required global::System.Guid taskId { get; init; }
        public required uint ordinal { get; init; }
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required global::System.Guid botId { get; init; }
        public required uint pc { get; init; }
        public required uint pri { get; init; }
        public required uint frame { get; init; }
        public required uint node { get; init; }
        public required uint state { get; init; }
        public required uint code { get; init; }
        public required uint Stop { get; init; }
        public required uint LatentIndex { get; init; }
        public required global::System.Collections.Generic.List<global::AFServer.AssaultFire.Matches.Bots.QueryObject> Identities { get; init; }
        public required int ProofCount { get; init; }
        public required bool PawnIndependent { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotNavigationStopJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationStop.PrepareAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationStop.PrepareAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotProgramRetirement))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotNavigationStopJsonContext : JsonSerializerContext;
}
