using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches;

sealed partial class AfdevNativeBootstrap
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record CaptureReadinessDiagnosticJson1
    {
        public required global::System.Guid instance { get; init; }
        public required global::System.Guid allocation { get; init; }
        public required global::System.Guid attempt { get; init; }
        public required uint pid { get; init; }
        public required global::System.DateTimeOffset process_created_utc { get; init; }
        public required global::System.DateTimeOffset observed_utc { get; init; }
        public required string phase { get; init; }
        public required global::System.DateTimeOffset? open_started_utc { get; init; }
        public required global::System.DateTimeOffset? open_completed_utc { get; init; }
        public required bool stable { get; init; }
        public required int changes { get; init; }
        public required global::System.Collections.Generic.SortedDictionary<string, string> snapshot { get; init; }
        public required global::System.DateTimeOffset? last_stable_observed_utc { get; init; }
        public required string? last_stable_snapshot { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevNativeBootstrapJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevNativeBootstrap.CaptureReadinessDiagnosticJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevNativeBootstrap.NativeNameDiagnostic))]
    [JsonSerializable(typeof(global::System.Collections.Generic.SortedDictionary<string, string>))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevNativeBootstrapJsonContext : JsonSerializerContext;
}
