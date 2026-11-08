using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotRouteRun
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required global::System.Guid routeId { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkStep? walkStep { get; init; }
        public required uint Pawn { get; init; }
        public required long PawnGeneration { get; init; }
        public required bool NativeReachability { get; init; }
        public required int MaximumSeconds { get; init; }
        public required int MaximumDistance { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotRouteRunJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteResult))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteRun.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteStep))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotRouteRunJsonContext : JsonSerializerContext;
}
