using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class FactoryInspector
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record SnapshotJson1
    {
        public required uint Address { get; init; }
        public required uint Node { get; init; }
        public required uint Object { get; init; }
        public required uint Code { get; init; }
        public required uint StateNode { get; init; }
        public required ushort LatentAction { get; init; }
    }

    private sealed record SnapshotJson2
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required string Class { get; init; }
        public required uint WorldInfo { get; init; }
        public required uint player { get; init; }
        public required uint pawn { get; init; }
        public required uint pri { get; init; }
        public required uint NavigationHandle { get; init; }
        public required object? StateFrame { get; init; }
        public required bool InActors { get; init; }
        public required uint ActorFlags { get; init; }
    }

    private sealed record SnapshotJson3
    {
        public required global::AFServer.AssaultFire.Matches.Bots.QueryObject Identity { get; init; }
        public required string Class { get; init; }
        public required uint Owner { get; init; }
        public required uint WorldInfo { get; init; }
        public required uint Team { get; init; }
        public required bool InActors { get; init; }
    }

    private sealed record SnapshotJson4
    {
        public required uint World { get; init; }
        public required uint ExpectedWorld { get; init; }
        public required uint GameInfo { get; init; }
        public required global::System.Collections.Generic.Dictionary<string, uint> counts { get; init; }
        public required uint Level { get; init; }
        public required uint ActorData { get; init; }
        public required uint ActorCount { get; init; }
        public required object? Controller { get; init; }
        public required object? Pri { get; init; }
    }

    private sealed record NativeSlotsJson1
    {
        public required uint Table { get; init; }
        public required global::System.Collections.Generic.Dictionary<int, uint> Slots { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => FactoryInspectorJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.NativeSlotsJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.SnapshotJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.SnapshotJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.SnapshotJson3))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.SnapshotJson4))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class FactoryInspectorJsonContext : JsonSerializerContext;
}
