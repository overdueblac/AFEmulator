using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotMotionRun
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record StartAsyncJson1
    {
        public required long Generation { get; init; }
        public required int IdentityCount { get; init; }
        public required int GuardCount { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.StartAsyncJson2[] Guards { get; init; }
    }

    private sealed record StartAsyncJson2
    {
        public required uint Address { get; init; }
        public required uint Value { get; init; }
        public required uint Mask { get; init; }
    }

    private sealed record StartAsyncJson3
    {
        public required long Generation { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required uint OriginalPhysics { get; init; }
        public required string OriginalPhysicsPrefix { get; init; }
        public required uint Observer { get; init; }
        public required string ObserverHex { get; init; }
        public required bool SharedObserver { get; init; }
        public required uint Trace { get; init; }
        public required uint BindingTick { get; init; }
        public required bool ObserverRetainedUntilOwnedJobStops { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceFunction Physics { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceFunction Stop { get; init; }
        public required int Guards { get; init; }
        public required uint PhysicsEntry { get; init; }
        public required int PhysicsBodyBytes { get; init; }
        public required string PhysicsBodySha256 { get; init; }
        public required string PhysicsCallingConvention { get; init; }
        public required bool IndexedPhysicsRejectedByProcessEvent { get; init; }
        public required uint PhysicsDispatchReturn { get; init; }
        public required bool PhysicsReturnsObservedAtVirtualSlot { get; init; }
        public required string MovementInput { get; init; }
    }

    private sealed record DisarmAsyncJson1
    {
        public required uint Before { get; init; }
        public required uint IdleAck { get; init; }
        public required double TotalMilliseconds { get; init; }
        public required string Reason { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotMotionRunJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionObservation))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.DisarmAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.StartAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.StartAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.StartAsyncJson3))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotMotionRunJsonContext : JsonSerializerContext;
}
