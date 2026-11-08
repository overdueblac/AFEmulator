using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotEngagement
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record UpdateAbilitiesAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatResult result { get; init; }
        public required string Basis { get; init; }
    }

    private sealed record UpdateAbilitiesAsyncJson2
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required string ClassPath { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Target { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatResult started { get; init; }
        public required string Basis { get; init; }
    }

    private sealed record UpdateAsyncJson1
    {
        public required uint Controller { get; init; }
        public required uint Pri { get; init; }
        public required long revision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.CombatLifecycle observed { get; init; }
    }

    private sealed record UpdateAsyncJson2
    {
        public required long revision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject? Pawn { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject? Target { get; init; }
        public required int Round { get; init; }
    }

    private sealed record DeferTargetJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Target { get; init; }
        public required global::System.DateTimeOffset until { get; init; }
        public required string reason { get; init; }
    }

    private sealed record DecisionJson1
    {
        public required long Sequence { get; init; }
        public required global::System.DateTimeOffset ObservedAtUtc { get; init; }
        public required string Reason { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatDecision decision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatSnapshot? Snapshot { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.CombatTarget? Target { get; init; }
        public required double? Distance { get; init; }
        public required double? AttackAttemptRange { get; init; }
        public required bool FireIntentActive { get; init; }
        public required bool AttackHitVerified { get; init; }
    }

    private sealed record DecisionJson2
    {
        public required global::System.DateTimeOffset ObservedAtUtc { get; init; }
        public required global::System.Collections.Generic.Dictionary<string, long> Counts { get; init; }
        public required string Limitation { get; init; }
    }

    private sealed record UpdateEvasionAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Pawn { get; init; }
        public required bool evading { get; init; }
        public required long revision { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.CombatTarget? Threat { get; init; }
        public required float TriggerDistance { get; init; }
        public required float ReleaseDistance { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotEngagementJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.DecisionJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.DecisionJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.DeferTargetJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.UpdateAbilitiesAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.UpdateAbilitiesAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.UpdateAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.UpdateAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.UpdateEvasionAsyncJson1))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotEngagementJsonContext : JsonSerializerContext;
}
