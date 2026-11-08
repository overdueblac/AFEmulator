using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotCombat
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record StopAsyncJson1
    {
        public required uint ordinal { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatResult? stopped { get; init; }
        public required bool NativeOwnerRetired { get; init; }
        public required uint IdleAck { get; init; }
    }

    private sealed record ExecuteLockedAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatResult result { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPoint? destination { get; init; }
        public required uint Status { get; init; }
        public required string HeaderHex { get; init; }
    }

    private sealed record ExecuteLockedAsyncJson2
    {
        public required uint ordinal { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatAction action { get; init; }
        public required uint targetPawn { get; init; }
        public required uint started { get; init; }
        public required uint returned { get; init; }
        public required string Message { get; init; }
    }

    private sealed record DescribeJson1
    {
        public required uint next { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatAction action { get; init; }
        public required uint targetPawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Weapon { get; init; }
        public required long PawnGeneration { get; init; }
        public required long WeaponGeneration { get; init; }
        public required string Function { get; init; }
        public required int Identities { get; init; }
        public required int Proofs { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotCombatJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombat.DescribeJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombat.ExecuteLockedAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombat.ExecuteLockedAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombat.StopAsyncJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotCombatJsonContext : JsonSerializerContext;
}
