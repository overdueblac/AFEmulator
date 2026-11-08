using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches;

sealed partial record AfdevInstanceWorkspace
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record PrepareCoreAsyncJson1
    {
        public required string state { get; init; }
        public required global::System.Guid instance_id { get; init; }
        public required global::System.Guid allocation_id { get; init; }
        public required ulong room_id { get; init; }
        public required int attempt_number { get; init; }
        public required global::System.Guid attempt_id { get; init; }
        public required uint mode_id { get; init; }
        public required ushort map_id { get; init; }
        public required string map { get; init; }
        public required string game { get; init; }
        public required int public_port { get; init; }
        public required int target_port { get; init; }
        public required string runtime_version { get; init; }
        public required string afdev_sha256 { get; init; }
        public required string servermove_v4_sha256 { get; init; }
        public required string mode_map_catalog_version { get; init; }
        public required string storage_layout { get; init; }
        public required string runtime_root { get; init; }
        public required string executable_path { get; init; }
        public required long private_resource_bytes { get; init; }
        public required long shared_resource_bytes { get; init; }
        public required int shared_file_count { get; init; }
    }

    private sealed record ArchiveAndRemoveStoppedResourcesJson1
    {
        public required string path { get; init; }
        public required long bytes { get; init; }
        public required string sha256 { get; init; }
    }

    private sealed record WriteRetentionJson1
    {
        public required string state { get; init; }
        public required global::System.Guid instance_id { get; init; }
        public required global::System.Guid allocation_id { get; init; }
        public required int stopped_resources_to_retain { get; init; }
        public required string? preserved_reserved_runtime { get; init; }
        public required global::System.DateTimeOffset observed_utc { get; init; }
        public required global::System.Collections.Generic.List<object> evidence { get; init; }
        public required string? error { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevInstanceWorkspaceJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevInstanceWorkspace.ArchiveAndRemoveStoppedResourcesJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevInstanceWorkspace.PrepareCoreAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevInstanceWorkspace.WriteRetentionJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevInstanceWorkspaceJsonContext : JsonSerializerContext;
}
