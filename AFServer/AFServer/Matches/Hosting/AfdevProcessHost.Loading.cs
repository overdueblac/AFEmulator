namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevProcessHost
{
    private readonly object loadingGate = new();
    private LoadingTimeline loadingTimeline = new();

    /// <summary>
    /// When this actual world first passed map, network, and binding checks, independently of human or bot
    /// Pawns.
    /// </summary>
    public DateTimeOffset? WorldReadyAtUtc => Volatile.Read(ref loadingTimeline).WorldReadyAtUtc;

    /// <summary>
    /// When this match's first human A11A endpoint was sent successfully. Host.BindAsync alone does not
    /// establish published network admission.
    /// </summary>
    public DateTimeOffset? EndpointPublishedAtUtc => Volatile.Read(ref loadingTimeline).EndpointPublishedAtUtc;

    /// <summary>
    /// When every bot's resources and waiting state were prepared and native activation armed.
    /// </summary>
    public DateTimeOffset? BotRosterPreparedAtUtc => Volatile.Read(ref loadingTimeline).BotRosterPreparedAtUtc;

    private void RecordWorldReady(int plannedBots)
        => RecordLoading("AFDEV_WORLD_READY", value => value with
        {
            WorldReadyAtUtc = DateTimeOffset.UtcNow, PlannedBots = plannedBots
        });

    /// <summary>
    /// Records the lobby's first successful A11A send without changing native preparation or granting
    /// player eligibility.
    /// </summary>
    internal void RecordEndpointPublished()
        => RecordLoading("AFDEV_ENDPOINT_PUBLISHED", value => value with { EndpointPublishedAtUtc = DateTimeOffset.UtcNow });

    private void RecordBotRosterPrepared(int preparedBots)
        => RecordLoading("BIO_BOT_ROSTER_READY", value => value with
        {
            BotRosterPreparedAtUtc = DateTimeOffset.UtcNow, PreparedBots = preparedBots
        });

    private void RecordFirstPawnReady(DateTimeOffset observed)
    {
        if (Volatile.Read(ref loadingTimeline).FirstPawnReadyAtUtc != null)
            return;

        RecordLoading("BIO_BOT_FIRST_PAWN_READY", value => value with { FirstPawnReadyAtUtc = observed });
    }

    private void RecordLoading(string phase, Func<LoadingTimeline, LoadingTimeline> update)
    {
        LoadingTimeline current;

        lock (loadingGate)
        {
            current = update(loadingTimeline);
            Volatile.Write(ref loadingTimeline, current);
            WriteAtomic("bot-loading.json", new RecordLoadingJson1 { InstanceId = InstanceId, attemptId = attemptId, allocationId = allocationId, roomId = roomId, Times = current });
        }

        ReportNative($"{phase} observed_utc={DateTimeOffset.UtcNow:O} world_ready_utc={current.WorldReadyAtUtc:O} " +
            $"endpoint_utc={current.EndpointPublishedAtUtc:O} roster_utc={current.BotRosterPreparedAtUtc:O} " +
            $"first_pawn_utc={current.FirstPawnReadyAtUtc:O} bots={current.PreparedBots}/{current.PlannedBots}");
    }

    private sealed record LoadingTimeline
    {
        public DateTimeOffset? WorldReadyAtUtc { get; init; }
        public DateTimeOffset? EndpointPublishedAtUtc { get; init; }
        public DateTimeOffset? BotRosterPreparedAtUtc { get; init; }
        public DateTimeOffset? FirstPawnReadyAtUtc { get; init; }
        public int PlannedBots { get; init; }
        public int PreparedBots { get; init; }
    }
}
