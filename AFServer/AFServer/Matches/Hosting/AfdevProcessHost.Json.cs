using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches;

sealed partial class AfdevProcessHost
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record RunBoundBotWorkAsyncJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid attemptId { get; init; }
        public required global::System.Guid allocationId { get; init; }
        public required ulong roomId { get; init; }
        public required global::System.DateTimeOffset ObservedAtUtc { get; init; }
        public required string Phase { get; init; }
        public required string? Kind { get; init; }
        public required string Message { get; init; }
        public required string? StackTrace { get; init; }
    }

    private sealed record SnapshotJson1
    {
        public required string readiness_contract { get; init; }
        public required global::System.Guid instance_id { get; init; }
        public required global::AFServer.AssaultFire.Matches.AfdevWorldBinding? world_binding { get; init; }
        public required string state { get; init; }
        public required string message { get; init; }
        public required global::System.DateTimeOffset observed_utc { get; init; }
        public required global::System.Guid allocation_id { get; init; }
        public required ulong room_id { get; init; }
        public required int attempt_number { get; init; }
        public required global::System.Guid attempt_id { get; init; }
        public required uint process_id { get; init; }
        public required global::System.DateTimeOffset? process_created_utc { get; init; }
        public required string? executable { get; init; }
        public required string? working_directory { get; init; }
        public required string? runtime_root { get; init; }
        public required bool? reserved_runtime { get; init; }
        public required int? public_port { get; init; }
        public required int? target_port { get; init; }
        public required uint mode_id { get; init; }
        public required ushort map_id { get; init; }
        public required string map { get; init; }
        public required string game_info { get; init; }
        public required string manifest_sha256 { get; init; }
        public required string runtime_version { get; init; }
        public required string afdev_sha256 { get; init; }
        public required string servermove_v4_sha256 { get; init; }
        public required string mode_map_catalog_version { get; init; }
        public required global::AFServer.AssaultFire.Matches.AfdevNativeEvidence? native { get; init; }
    }

    private sealed record RecordLoadingJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid attemptId { get; init; }
        public required global::System.Guid allocationId { get; init; }
        public required ulong roomId { get; init; }
        public required global::AFServer.AssaultFire.Matches.AfdevProcessHost.LoadingTimeline Times { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevProcessHostJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevProcessHost.RecordLoadingJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevProcessHost.RunBoundBotWorkAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevProcessHost.SnapshotJson1))]
    [JsonSerializable(typeof(object))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevProcessHostJsonContext : JsonSerializerContext;
}
