using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotReachQuery
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required uint next { get; init; }
        public required uint goal { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? point { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject pawn { get; init; }
        public required string HeaderHex { get; init; }
        public required uint CompletedOrdinal { get; init; }
    }

    private sealed record ExecuteAsyncJson2
    {
        public required uint ordinal { get; init; }
        public required uint goal { get; init; }
        public required uint expectedPawn { get; init; }
        public required uint started { get; init; }
        public required uint returned { get; init; }
        public required bool targetChanged { get; init; }
        public required string Message { get; init; }
    }

    private sealed record ExecuteAsyncJson3
    {
        public required uint ordinal { get; init; }
        public required uint goal { get; init; }
        public required string Message { get; init; }
        public required string Detail { get; init; }
    }

    private sealed record DescribeJson1
    {
        public required uint next { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject? Goal { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? Point { get; init; }
        public required uint function { get; init; }
        public required uint vtable { get; init; }
        public required uint goalVtable { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.QueryObject> Identities { get; init; }
        public required int ProofCount { get; init; }
        public required uint NativeExec { get; init; }
        public required string RuntimeScriptHex { get; init; }
        public required string RuntimeScriptSha256 { get; init; }
        public required string DescriptorHex { get; init; }
        public required uint NativeResultSentinel { get; init; }
        public required string Parameters { get; init; }
        public required string NativeImageSha256 { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotReachQueryJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachQuery.DescribeJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachQuery.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachQuery.ExecuteAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachQuery.ExecuteAsyncJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachResult))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotReachQueryJsonContext : JsonSerializerContext;
}
