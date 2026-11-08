using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotWalkGraph
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record RecommendJson1
    {
        public required int Index { get; init; }
        public required double Distance { get; init; }
    }

    private sealed record RecommendJson2
    {
        public required int Index { get; init; }
        public required double Distance { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? From { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotWalkGraphJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkGraph.RecommendJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkGraph.RecommendJson2))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotWalkGraphJsonContext : JsonSerializerContext;
}
