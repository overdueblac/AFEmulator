using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches;

sealed partial class AfdevLeaseRelay
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    internal static IJsonTypeInfoResolver JsonMetadata => AfdevLeaseRelayJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.AfdevNativePlayerEvidence))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevLeaseRelayJsonContext : JsonSerializerContext;
}
