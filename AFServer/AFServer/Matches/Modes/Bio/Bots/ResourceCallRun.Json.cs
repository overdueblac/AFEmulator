using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

static partial class ResourceCallRun
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceMethod method { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.QueryObject> identities { get; init; }
        public required uint commandAddress { get; init; }
        public required uint parms { get; init; }
        public required uint inputString { get; init; }
        public required string ParametersHex { get; init; }
        public required string CommandHex { get; init; }
        public required string ScriptHex { get; init; }
        public required string ScriptSha256 { get; init; }
    }

    private sealed record ExecuteAsyncJson2
    {
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceProfile profile { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObservation observation { get; init; }
        public required global::System.Collections.Generic.List<string> completed { get; init; }
    }

    private sealed record ExecuteAsyncJson3
    {
        public required bool independent { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceString Uin { get; init; }
        public required uint BorrowedInputData { get; init; }
        public required string ParametersAfter { get; init; }
        public required string InputAfter { get; init; }
        public required bool RetainedUntilOwnedProcessStops { get; init; }
    }

    private sealed record InvokeAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObservation Observation { get; init; }
        public required uint NativeCompletedOrdinal { get; init; }
        public required string CommandHex { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => ResourceCallRunJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.CommandIdleAckReceipt))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceAvatarResult))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceCallRun.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceCallRun.ExecuteAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceCallRun.ExecuteAsyncJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceCallRun.InvokeAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceMethod))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceProfile))]
    [JsonSerializable(typeof(global::System.Collections.Generic.List<global::AFServer.AssaultFire.Matches.Bots.QueryObservation>))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class ResourceCallRunJsonContext : JsonSerializerContext;
}
