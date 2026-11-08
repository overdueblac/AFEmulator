using System.Diagnostics;
using AFConnect.Proxy;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    private async ValueTask RejectPreparationAsync(GameSession session, GameRoom room, ushort responseCommand,
        CancellationToken cancellationToken)
    {
        if (allocator is IMatchPreparationAllocator)
        {
            await SendAsync(session, 0xA122, new TdrWriter().U32(0), cancellationToken);
            if (startingMatches.TryGetValue(room.RoomId, out var pending))
                await ReportPreparationAsync(pending, pending.Stage, cancellationToken);
            else if (session.MatchTransport is IMatchPreparationTransport transport)
            {
                // A rejection registers only one pair of prompts. It never allocates a world or replaces another active preparation identity.
                Guid rejected = Guid.NewGuid();

                await transport.ReportPreparationAsync(rejected, AfdevPreparationStage.Queued, 0, 1, 0,
                    static () => { }, cancellationToken);
                await transport.ReportPreparationAsync(rejected, AfdevPreparationStage.Failed, 0, 1, 0,
                    static () => { }, cancellationToken);
            }
        }
        await SendAsync(session, responseCommand, new TdrWriter().U16(0x8101), cancellationToken);
    }

    private async ValueTask ReportPreparationAsync(MatchStartOperation operation, AfdevPreparationStage stage,
        CancellationToken cancellationToken)
    {
        if (allocator is not IMatchPreparationAllocator runtime) return;
        // Once decided, a terminal state cannot be replayed or rewritten. New requests during cleanup cannot revive an old operation's notification identity.
        if (operation.Stage is AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Completed)
            return;

        var progress = runtime.GetPreparation(operation.Snapshot.AllocationId);
        var remaining = Math.Clamp((int)Math.Ceiling((operation.Timeout -
            Stopwatch.GetElapsedTime(operation.StartedAt)).TotalSeconds), 0, ushort.MaxValue);

        operation.Stage = stage;
        foreach (var member in operation.Snapshot.Members)
        {
            if (operation.PreparationFinished.Contains(member.Session) || member.Session.IsDisconnected ||
                member.Session.MatchTransport is not IMatchPreparationTransport transport) continue;

            var terminal = stage is AfdevPreparationStage.Cancelled or AfdevPreparationStage.Failed or AfdevPreparationStage.Completed;

            // A failed write may still have reached the peer. The delivery attempt defines finality; never send another terminal state to the same session.
            if (terminal) operation.PreparationFinished.Add(member.Session);
            try
            {
                await transport.ReportPreparationAsync(operation.Snapshot.AllocationId, stage,
                    checked((ushort)Math.Min(progress.Attempt, progress.Maximum)), checked((ushort)progress.Maximum),
                    checked((ushort)remaining), () => _ = CancelPreparationAsync(operation), cancellationToken);
            }
            catch (Exception error) when (terminal)
            {
                protocolLog?.Invoke($"准备终态交付未确认 session={member.Session.SessionId:N} allocation={operation.Snapshot.AllocationId:N} stage={stage}：{error.GetType().Name}。");
            }
        }
    }

    // The control-frame reader must not await the lobby lock. The same dispatchGate decides cancellation and publication.
    private async Task CancelPreparationAsync(MatchStartOperation operation)
    {
        await dispatchGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(startingMatches.GetValueOrDefault(operation.Room.RoomId), operation)) return;

            operation.CancelRequested = 1;
            InvalidateMatchStart(operation.Room);
        }
        catch (Exception error) { protocolLog?.Invoke($"准备取消处理失败：{error.GetType().Name}。"); }
        finally { dispatchGate.Release(); }
    }

    private async Task TrackPreparationAsync(MatchStartOperation operation)
    {
        var runtime = (IMatchPreparationAllocator)allocator;
        var previous = (State: AfdevRuntimeState.Created, Attempt: 0, Maximum: 0);

        try
        {
            while (true)
            {
                await Task.Delay(500, operation.ProgressStopping.Token);
                var progress = runtime.GetPreparation(operation.Snapshot.AllocationId);

                if (progress == previous) continue;

                var stage = progress.State switch
                {
                    AfdevRuntimeState.Created => AfdevPreparationStage.Queued,
                    AfdevRuntimeState.Preparing or AfdevRuntimeState.Prepared =>
                        progress.Attempt > 1 ? AfdevPreparationStage.Retrying : AfdevPreparationStage.Copying,
                    AfdevRuntimeState.Stopping or AfdevRuntimeState.CleanupPending or AfdevRuntimeState.Failed =>
                        AfdevPreparationStage.Retrying,
                    _ => AfdevPreparationStage.Starting
                };

                await dispatchGate.WaitAsync(operation.ProgressStopping.Token);
                try
                {
                    CheckStartOperation(operation);
                    await ReportPreparationAsync(operation, stage, operation.ProgressStopping.Token);
                }
                finally { dispatchGate.Release(); }
                previous = progress;
            }
        }
        catch (OperationCanceledException) when (operation.ProgressStopping.IsCancellationRequested || operation.Stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            protocolLog?.Invoke($"准备状态交付失败，取消本操作：{error.GetType().Name}。");
            await CancelPreparationAsync(operation);
        }
    }

    private static async Task StopPreparationProgressAsync(MatchStartOperation operation)
    {
        operation.ProgressStopping.Cancel();
        await operation.Progress;
    }
}
