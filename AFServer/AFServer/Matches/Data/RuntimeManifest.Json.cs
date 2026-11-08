using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches;

sealed partial class RuntimeManifest
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    internal static IJsonTypeInfoResolver JsonMetadata => RuntimeManifestJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.RuntimeManifest))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class RuntimeManifestJsonContext : JsonSerializerContext;
}
