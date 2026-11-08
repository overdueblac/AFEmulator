using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFServer.AssaultFire.Matches.Bots;

sealed partial class AfdevBioBotRuntime
{
    // Fixed diagnostic DTOs preserve existing JSON fields and order for compile-time Native AOT metadata generation.
    private sealed record ArmAsyncJson1
    {
        public required uint Command { get; init; }
        public required uint timerEntry { get; init; }
        public required string CommandHex { get; init; }
        public required bool SharedTimer { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWaitingEvidence ready { get; init; }
    }

    private sealed record ArmAsyncJson2
    {
        public required global::System.Guid AllocationId { get; init; }
        public required ulong RoomId { get; init; }
        public required global::System.Guid BotId { get; init; }
        public required string Uin { get; init; }
        public required global::System.Guid[] HumanSessionIds { get; init; }
        public required uint Controller { get; init; }
        public required uint Pri { get; init; }
        public required bool BotReady { get; init; }
        public required bool AwaitingExactHuman { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWaitingEvidence ready { get; init; }
        public required uint timerEntry { get; init; }
        public required int Slot { get; init; }
        public required byte FighterCapacity { get; init; }
        public required int PlannedBots { get; init; }
    }

    private sealed record SaveActivationFailureJson1
    {
        public required uint Address { get; init; }
        public required uint Expected { get; init; }
        public required uint Actual { get; init; }
    }

    private sealed record SaveActivationFailureJson2
    {
        public required uint Command { get; init; }
        public required string CommandHex { get; init; }
        public required global::System.Collections.Generic.List<object> proofs { get; init; }
    }

    private sealed record CheckParticipantJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required uint Controller { get; init; }
        public required uint Pri { get; init; }
        public required uint Status { get; init; }
        public required uint HumanConnection { get; init; }
        public required uint HumanController { get; init; }
        public required uint HumanPri { get; init; }
        public required bool ClientVisibilityValidated { get; init; }
    }

    private sealed record StopParticipantAsyncJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid attemptId { get; init; }
        public required uint ProcessId { get; init; }
        public required global::System.DateTimeOffset CreationTimeUtc { get; init; }
        public required bool AnyCommandWasPublished { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.CommandIdleAckReceipt? resourcesIdle { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.CommandIdleAckReceipt? lifecycleIdle { get; init; }
        public required bool PagesRetainedUntilOwnedJobStops { get; init; }
        public required bool BotReady { get; init; }
    }

    private sealed record InstallSuspendedJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid attemptId { get; init; }
        public required uint ProcessId { get; init; }
        public required global::System.DateTimeOffset CreationTimeUtc { get; init; }
        public required uint code { get; init; }
        public required uint trace { get; init; }
        public required uint timerEntry { get; init; }
        public required uint preparationCommand { get; init; }
        public required uint preparationStartEntry { get; init; }
        public required uint scriptDispatchEntry { get; init; }
        public required uint ScriptDispatchCallSite { get; init; }
        public required string ScriptDispatchSha256 { get; init; }
        public required int wrapperCapacity { get; init; }
        public required int SlotCapacity { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.InstallSuspendedJson2[] Slots { get; init; }
        public required string WrapperSha256 { get; init; }
        public required string ImageSha256 { get; init; }
        public required string AuditedImageSha256 { get; init; }
        public required bool PagesRetainedUntilOwnedJobStops { get; init; }
    }

    private sealed record InstallSuspendedJson2
    {
        public required int slotIndex { get; init; }
        public required uint factoryCommand { get; init; }
        public required uint resourceCommand { get; init; }
        public required uint lifecycleCommand { get; init; }
        public required uint activationCommand { get; init; }
        public required uint navigationCommand { get; init; }
        public required uint motionCommand { get; init; }
        public required uint reachCommand { get; init; }
        public required uint navigationStopCommand { get; init; }
        public required uint controlCommand { get; init; }
        public required uint combatCommand { get; init; }
        public required uint onlineIsolationPage { get; init; }
    }

    private sealed record PrepareParticipantAsyncJson1
    {
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPreparation result { get; init; }
        public required global::System.Collections.Generic.IReadOnlyDictionary<string, bool> Checks { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceInventoryResult inventory { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceSkillResult skills { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.ResourceLoadingState loading { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.TickSample Tick { get; init; }
    }

    private sealed record ObserveFirstPawnJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required bool PawnReady { get; init; }
        public required global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.FirstPawnEvidence FirstPawn { get; init; }
        public required bool ClientVisibilityValidated { get; init; }
    }

    private sealed record InstallOnlineIsolationSuspendedJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid attemptId { get; init; }
        public required uint ProcessId { get; init; }
        public required global::System.DateTimeOffset CreationTimeUtc { get; init; }
        public required uint code { get; init; }
        public required uint[] pages { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<uint> ConfirmationEntries { get; init; }
        public required string ProgramSha256 { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.AfdevOnlineDsIsolationHook.Patch> Patches { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.AfdevOnlineDsIsolationHook.Entry> Entries { get; init; }
        public required bool IdentityPublished { get; init; }
        public required bool PagesRetainedUntilOwnedJobStops { get; init; }
    }

    private sealed record PublishOnlineIdentityJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required global::System.Guid BotId { get; init; }
        public required string Uin { get; init; }
        public required uint controller { get; init; }
        public required uint Page { get; init; }
    }

    private sealed record DisarmOnlineIdentityConfirmationAsyncJson1
    {
        public required uint BeforeIdleAck { get; init; }
        public required uint AfterIdleAck { get; init; }
        public required uint Started { get; init; }
        public required uint Completed { get; init; }
        public required uint Status { get; init; }
        public required bool PagesRetainedUntilOwnedJobStops { get; init; }
    }

    private sealed record BeginRosterPreparationAsyncJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required ulong RoomId { get; init; }
        public required byte FighterCapacity { get; init; }
        public required int PlannedBots { get; init; }
        public required uint preparationCommand { get; init; }
        public required uint preparationStartEntry { get; init; }
        public required uint sharedTimerEntry { get; init; }
        public required string CommandHex { get; init; }
        public required bool WorldReady { get; init; }
        public required bool BotRosterPrepared { get; init; }
        public required bool PawnReady { get; init; }
    }

    private sealed record BeginRosterPreparationAsyncJson2
    {
        public required global::System.DateTimeOffset AtUtc { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required ulong RoomId { get; init; }
        public required byte FighterCapacity { get; init; }
        public required int PlannedBots { get; init; }
        public required double ElapsedMilliseconds { get; init; }
        public required bool WorldReady { get; init; }
        public required bool BotRosterPrepared { get; init; }
        public required bool PawnReady { get; init; }
        public required bool OriginalTimerContinues { get; init; }
        public required bool NativeStartRequestHeld { get; init; }
    }

    private sealed record CompleteRosterPreparationAsyncJson1
    {
        public required global::System.DateTimeOffset AtUtc { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required ulong RoomId { get; init; }
        public required int PlannedBots { get; init; }
        public required bool ResourcesAndActivationPublished { get; init; }
        public required bool AwaitingNativeRegistrationAndHuman { get; init; }
        public required bool PawnReady { get; init; }
    }

    private sealed record CheckRosterPreparationJson1
    {
        public required global::System.DateTimeOffset ObservedAtUtc { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required int PlannedBots { get; init; }
        public required uint BlockedStartRequests { get; init; }
        public required bool BotRosterPrepared { get; init; }
        public required bool NativeStartRequestReleased { get; init; }
        public required bool PawnReady { get; init; }
    }

    private sealed record StopRosterPreparationAsyncJson1
    {
        public required global::System.DateTimeOffset AtUtc { get; init; }
        public required uint? World { get; init; }
        public required bool IdleAcknowledged { get; init; }
        public required bool PagesRetainedUntilOwnedJobStops { get; init; }
    }

    private sealed record PrepareRosterAsyncJson1
    {
        public required global::System.Guid InstanceId { get; init; }
        public required global::System.Guid AttemptId { get; init; }
        public required global::System.Guid AllocationId { get; init; }
        public required long WorldGeneration { get; init; }
        public required ulong RoomId { get; init; }
        public required byte FighterCapacity { get; init; }
        public required int FrozenHumanFighters { get; init; }
        public required int Observers { get; init; }
        public required global::System.Collections.Generic.IReadOnlyList<global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPreparation> Bots { get; init; }
        public required bool WorldReady { get; init; }
        public required bool BotRosterPrepared { get; init; }
        public required bool PawnReady { get; init; }
    }

    internal static IJsonTypeInfoResolver JsonMetadata => AfdevBioBotRuntimeJsonContext.Default;

    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombatEntryCheck))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.ArmAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.ArmAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.BeginRosterPreparationAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.BeginRosterPreparationAsyncJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.CheckParticipantJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.CheckRosterPreparationJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.CompleteRosterPreparationAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.DisarmOnlineIdentityConfirmationAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.InstallOnlineIsolationSuspendedJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.InstallSuspendedJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.InstallSuspendedJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.ObserveFirstPawnJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.PrepareParticipantAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.PrepareRosterAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.PublishOnlineIdentityJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.SaveActivationFailureJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.SaveActivationFailureJson2))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.StopParticipantAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.StopRosterPreparationAsyncJson1))]
    [JsonSerializable(typeof(global::AFServer.AssaultFire.Matches.Bots.AfdevOnlineDsIsolationSnapshot))]
    [JsonSerializable(typeof(object))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    private partial class AfdevBioBotRuntimeJsonContext : JsonSerializerContext;
}
