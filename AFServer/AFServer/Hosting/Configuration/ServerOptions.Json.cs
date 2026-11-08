using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Hosting;

sealed partial class ServerOptions
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    internal static IJsonTypeInfoResolver JsonMetadata => ServerOptionsJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Hosting.ServerOptions))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class ServerOptionsJsonContext : JsonSerializerContext;
}
