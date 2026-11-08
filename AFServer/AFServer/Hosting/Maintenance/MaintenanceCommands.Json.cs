using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Hosting;

static partial class MaintenanceCommands
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    internal static IJsonTypeInfoResolver JsonMetadata => MaintenanceCommandsJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.MatchServerOptions))]
    [JsonSerializable(typeof(global::System.Text.Json.Nodes.JsonObject))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class MaintenanceCommandsJsonContext : JsonSerializerContext;
}
