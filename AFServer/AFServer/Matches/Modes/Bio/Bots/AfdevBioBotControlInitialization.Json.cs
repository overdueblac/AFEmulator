using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

static partial class AfdevBioBotControlInitialization
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required uint ProcessId { get; init; }
        public required global::System.DateTimeOffset ProcessCreationTimeUtc { get; init; }
        public required string AfdevSha256 { get; init; }
        public required uint controller { get; init; }
        public required global::System.Guid botId { get; init; }
        public required string uin { get; init; }
        public required uint onlinePage { get; init; }
        public required uint command { get; init; }
        public required uint preparationCommand { get; init; }
        public required string DescriptorHex { get; init; }
        public required string Basis { get; init; }
        public required bool ControlledComparisonOnly { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotControlInitializationJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotControlInitialization.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotControlReceipt))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotControlInitializationJsonContext : JsonSerializerContext;
}
