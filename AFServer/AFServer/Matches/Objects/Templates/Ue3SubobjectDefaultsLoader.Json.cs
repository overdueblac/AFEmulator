using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Objects;

static partial class Ue3SubobjectDefaultsLoader
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ReadObjectJson1
    {
        public required string status { get; init; }
    }

    private sealed record DecodeStructJson1
    {
        public required string status { get; init; }
        public required string reason { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ObjectName? serialized_type_metadata { get; init; }
    }

    private sealed record DecodeStructJson2
    {
        public required string name { get; init; }
        public required float value { get; init; }
        public required string bits_u32_hex { get; init; }
        public required int payload_logical_offset { get; init; }
        public required int payload_bytes { get; init; }
        public required string payload_sha256 { get; init; }
        public required global::System.Text.Json.JsonElement declaration { get; init; }
    }

    private sealed record DecodeStructJson3
    {
        public required string status { get; init; }
        public required string encoding { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ExportIdentity struct_identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ObjectSourceLocation struct_source { get; init; }
        public required string struct_flags_u32_hex { get; init; }
        public required global::System.Collections.Generic.Dictionary<string, float> value { get; init; }
        public required global::System.Collections.Generic.List<object> components { get; init; }
    }

    private sealed record DecodeStructJson4
    {
        public required string status { get; init; }
        public required string encoding { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ExportIdentity struct_identity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ObjectSourceLocation struct_source { get; init; }
        public required string struct_flags_u32_hex { get; init; }
        public required bool complete_runtime_value { get; init; }
        public required global::System.Collections.Immutable.ImmutableArray<global::AFServer.AssaultFire.Matches.Objects.Ue3ExplicitDefaultEvidence> members { get; init; }
        public required global::System.Collections.Immutable.ImmutableArray<global::AFServer.AssaultFire.Matches.Objects.Ue3ObjectName> missing_members_not_defaulted { get; init; }
    }

    private sealed record DescribeBindingJson1
    {
        public required string status { get; init; }
        public required long package_id { get; init; }
        public required string package { get; init; }
        public required int export_index { get; init; }
        public required string path { get; init; }
        public required string declared_kind { get; init; }
        public required int array_dimension { get; init; }
        public required global::AFServer.AssaultFire.Matches.Objects.Ue3ObjectSourceLocation source { get; init; }
        public required bool serialized_type_matches_declared_kind { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => Ue3SubobjectDefaultsLoaderJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.DecodeStructJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.DecodeStructJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.DecodeStructJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.DecodeStructJson4))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.DescribeBindingJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.ReadObjectJson1))]
    [JsonSerializable(typeof(global::System.Collections.Generic.Dictionary<string, object?>))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class Ue3SubobjectDefaultsLoaderJsonContext : JsonSerializerContext;
}
