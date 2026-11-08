using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

static partial class FactoryRun
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.FactoryFunction function { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.FactoryLocation location { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.QueryObject> identities { get; init; }
        public required uint commandAddress { get; init; }
        public required uint parms { get; init; }
        public required string ParametersHex { get; init; }
        public required string ScriptHex { get; init; }
        public required string ScriptSha256 { get; init; }
        public required string CommandHex { get; init; }
    }

    private sealed record ExecuteAsyncJson2
    {
        public required int IdentityCount { get; init; }
        public required int IdentityEnd { get; init; }
        public required int PropertyStart { get; init; }
        public required int ProofCount { get; init; }
        public required int ProofEnd { get; init; }
        public required int CommandSize { get; init; }
        public required int ParameterStart { get; init; }
        public required bool Published { get; init; }
    }

    private sealed record ExecuteAsyncJson3
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObservation Observation { get; init; }
        public required string CommandHex { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => FactoryRunJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryRun.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryRun.ExecuteAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryRun.ExecuteAsyncJson3))]
    [JsonSerializable(typeof(global::System.Collections.Generic.List<global::AFServer.AssaultFire.Matches.Bots.QueryObservation>))]
    [JsonSerializable(typeof(object))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class FactoryRunJsonContext : JsonSerializerContext;
}
