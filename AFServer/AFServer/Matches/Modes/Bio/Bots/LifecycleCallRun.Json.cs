using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

static partial class LifecycleCallRun
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ExecuteAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceFunction method { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.LifecycleOperation operation { get; init; }
        public required uint ordinal { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.QueryObject> identities { get; init; }
        public required global::System.Collections.Generic.List<global::AFServer.AssaultFire.Matches.Bots.LifecycleProof> proofs { get; init; }
        public required uint frame { get; init; }
        public required uint state { get; init; }
        public required uint managerField { get; init; }
        public required uint managerClass { get; init; }
        public required uint managerOwnerOffset { get; init; }
        public required uint managerWorldOffset { get; init; }
        public required string CommandHex { get; init; }
        public required string ParametersHex { get; init; }
        public required string ScriptHex { get; init; }
    }

    private sealed record ExecuteAsyncJson2
    {
        public required uint Manager { get; init; }
        public required uint inventoryPolicyOffset { get; init; }
        public required uint inventoryPolicyMask { get; init; }
        public required uint Before { get; init; }
        public required uint After { get; init; }
    }

    private sealed record ExecuteAsyncJson3
    {
        public required uint nameAddress { get; init; }
        public required uint nameData { get; init; }
        public required uint nameCount { get; init; }
        public required string? Nickname { get; init; }
        public required bool IndependentEngineString { get; init; }
        public required uint BorrowedInput { get; init; }
    }

    private sealed record ExecuteAsyncJson4
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Manager { get; init; }
        public required bool ReusedExistingManager { get; init; }
        public required bool NativeSpawnInvoked { get; init; }
        public required uint World { get; init; }
        public required uint GameInfo { get; init; }
    }

    private sealed record ExecuteAsyncJson5
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObservation Observation { get; init; }
        public required uint CompletedOrdinal { get; init; }
        public required string CommandHex { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => LifecycleCallRunJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.CommandIdleAckReceipt))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallResult))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.ExecuteAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.ExecuteAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.ExecuteAsyncJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.ExecuteAsyncJson4))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.ExecuteAsyncJson5))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.ResourceLoadingState))]
    [JsonSerializable(typeof(global::System.Collections.Generic.List<global::AFServer.AssaultFire.Matches.Bots.QueryObservation>))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class LifecycleCallRunJsonContext : JsonSerializerContext;
}
