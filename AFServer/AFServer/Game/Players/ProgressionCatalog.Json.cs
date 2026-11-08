using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Game;

sealed partial class ProgressionCatalog
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    internal static IJsonTypeInfoResolver JsonMetadata => ProgressionCatalogJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Game.ProgressionCatalog.Seed))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class ProgressionCatalogJsonContext : JsonSerializerContext;
}
