using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotWalkRoutes
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExtractRecordingJson1
    {
        public required float Radius { get; init; }
        public required float Height { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint[] Points { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotWalkRoutesJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkArchive))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkRoutes.ExtractRecordingJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotWalkRoutesJsonContext : JsonSerializerContext;
}
