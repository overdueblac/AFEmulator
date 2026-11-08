using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioPlayerRouteRecorder
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record RunAsyncJson1
    {
        public required string Kind { get; init; }
        public required int FormatVersion { get; init; }
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required uint ModeId { get; init; }
        public required ushort MapId { get; init; }
        public required string MapName { get; init; }
        public required string AfdevSha256 { get; init; }
        public required string MainMapSha256 { get; init; }
        public required global::System.DateTimeOffset StartedAtUtc { get; init; }
        public required int SampleIntervalMs { get; init; }
        public required long MaximumBytes { get; init; }
        public required int MaximumMinutes { get; init; }
        public required int HumanSlots { get; init; }
        public required bool VerifiedForBotReplay { get; init; }
    }

    private sealed record RunAsyncJson2
    {
        public required string Kind { get; init; }
        public required long Sequence { get; init; }
        public required long TimeMs { get; init; }
        public required global::System.DateTimeOffset ObservedAtUtc { get; init; }
        public required int HumanSlot { get; init; }
        public required long Segment { get; init; }
        public required bool SegmentStart { get; init; }
        public required int Round { get; init; }
        public required int Team { get; init; }
        public required string PawnClass { get; init; }
        public required float[] Location { get; init; }
        public required float[] Velocity { get; init; }
        public required byte Physics { get; init; }
        public required float Radius { get; init; }
        public required float Height { get; init; }
    }

    private sealed record RunAsyncJson3
    {
        public required long Samples { get; init; }
        public required long Bytes { get; init; }
        public required long SkippedSnapshots { get; init; }
        public required string Reason { get; init; }
        public required long DurationMs { get; init; }
        public required global::System.DateTimeOffset StoppedAtUtc { get; init; }
        public required global::System.Collections.Generic.Dictionary<int, long> Segments { get; init; }
        public required bool VerifiedForBotReplay { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioPlayerRouteRecorderJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioPlayerRouteRecorder.RunAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioPlayerRouteRecorder.RunAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioPlayerRouteRecorder.RunAsyncJson3))]
    [JsonSerializable(typeof(object))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioPlayerRouteRecorderJsonContext : JsonSerializerContext;
}
