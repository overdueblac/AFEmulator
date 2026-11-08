using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Hosting;

sealed partial class ClientPreflight
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record EvaluateAsyncJson1
    {
        public required string[] file_values { get; init; }
        public required string? resolved { get; init; }
    }

    private sealed record WriteGateAsyncJson1
    {
        public required int schema { get; init; }
        public required long checked_at_unix { get; init; }
        public required int server_pid { get; init; }
        public required bool passed { get; init; }
        public required bool listeners_ready { get; init; }
        public required bool log_written { get; init; }
        public required bool launch_ready { get; init; }
        public required global::System.Collections.Generic.Dictionary<string, bool> checks { get; init; }
        public required string client_root { get; init; }
        public required string tcls_path { get; init; }
        public required string? tcls_sha256 { get; init; }
        public required string apclient_path { get; init; }
        public required string private_key_path { get; init; }
        public required string server_log_path { get; init; }
        public required global::System.Collections.Generic.Dictionary<string, object> hosts { get; init; }
        public required global::System.Collections.Generic.List<string> errors { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => ClientPreflightJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Hosting.ClientPreflight.EvaluateAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Hosting.ClientPreflight.WriteGateAsyncJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class ClientPreflightJsonContext : JsonSerializerContext;
}
