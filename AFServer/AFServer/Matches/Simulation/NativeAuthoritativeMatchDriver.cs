using System.Numerics;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// One bounded clock advance. Ticks retains events/snapshots for every committed tick, not just the final
/// one.
/// </summary>
public sealed record NativeAuthoritativeAdvanceResult(NativeMatchSnapshot Snapshot,
    IReadOnlyList<NativeMatchTickResult> Ticks, TimeSpan BufferedElapsed);

/// <summary>
/// World-query or other failure stops clock driving. The authoritative core atomically rejects the failed
/// tick, but preceding ticks remain committed. Callers must process/audit CompletedTicks; never claim the
/// full catch-up batch rolled back.
/// </summary>
public sealed class NativeAuthoritativeDriverException : Exception
{
    public IReadOnlyList<NativeMatchTickResult> CompletedTicks { get; }
    public NativeMatchSnapshot Snapshot { get; }

    internal NativeAuthoritativeDriverException(Exception innerException,
        IReadOnlyList<NativeMatchTickResult> completedTicks, NativeMatchSnapshot snapshot)
        : base("权威固定时钟推进失败；驱动已终止，此前成功的 tick 没有回滚。", innerException)
    {
        CompletedTicks = completedTicks;
        Snapshot = snapshot;
    }
}

/// <summary>
/// Converts a server monotonic clock into fixed ticks for the bounded authoritative core. Rules/core share
/// construction; never exposes the core for another scheduler's AdvanceTick. now must come from a monotonic
/// source such as Stopwatch, not clients or DateTime. Pre-Start configuration time is excluded. Each
/// advance has an explicit compute budget and preserves remaining work; repeated now can drain backlog
/// without skipping physics/cooldown ticks. Implements no actual-rule parsing, UDP, RPC, authentication, or
/// production protocol factory.
/// </summary>
public sealed class NativeAuthoritativeMatchDriver
{
    // Memory/compute limits for this implementation, not native tick rates, capacities, or network command windows.
    public const int MaximumCatchUpTicks = 256;
    public const int MaximumBacklogTicks = 4096;

    private readonly object gate = new();
    private readonly NativeAuthoritativeMatch match;
    private readonly long fixedTickTicks;
    private long lastNowTicks;
    private long bufferedElapsedTicks;
    private bool started;
    private bool advancing;
    private bool faulted;

    public TimeSpan FixedTick { get; }
    public int MaximumTicksPerAdvance { get; }
    /// <summary>Maximum queued complete ticks, plus one exact remainder smaller than FixedTick.</summary>
    public int MaximumBufferedTicks { get; }
    public TimeSpan BufferedElapsed { get { lock (gate) return TimeSpan.FromTicks(bufferedElapsedTicks); } }
    public TimeSpan? LastObservedTime { get { lock (gate) return started ? TimeSpan.FromTicks(lastNowTicks) : null; } }
    public bool IsFaulted { get { lock (gate) return faulted; } }

    public NativeAuthoritativeMatchDriver(NativeAuthoritativeRules rules, INativeCollisionScene scene,
        int maximumTicksPerAdvance, int maximumBufferedTicks)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(scene);
        if (maximumTicksPerAdvance is < 1 or > MaximumCatchUpTicks)
            throw new ArgumentOutOfRangeException(nameof(maximumTicksPerAdvance));
        if (maximumBufferedTicks < maximumTicksPerAdvance || maximumBufferedTicks > MaximumBacklogTicks)
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedTicks));
        match = new(rules, scene);
        FixedTick = rules.FixedTick;
        fixedTickTicks = FixedTick.Ticks;
        MaximumTicksPerAdvance = maximumTicksPerAdvance;
        MaximumBufferedTicks = maximumBufferedTicks;
    }

    /// <summary>
    /// The caller must already authenticate. Retain returned bindings only in that identity's current
    /// authenticated session.
    /// </summary>
    public NativePlayerBinding BindAuthenticatedPlayer(string authenticatedOwnerIdentity, int playerId,
        int teamId, IReadOnlyList<Vector3> spawnPositions)
    {
        lock (gate)
        {
            RequireNotAdvancing();
            // Count/indexers can reenter this driver too. Guard the entire registration path before callbacks alter clock state ahead of core rejection.
            advancing = true;
            try
            {
                return match.BindAuthenticatedPlayer(authenticatedOwnerIdentity, playerId, teamId, spawnPositions);
            }
            finally { advancing = false; }
        }
    }

    /// <summary>
    /// Anchors time only after all players spawn successfully. Failed starts consume no server time.
    /// </summary>
    public NativeMatchSnapshot Start(TimeSpan now)
    {
        ValidateNow(now);
        lock (gate)
        {
            RequireNotAdvancing();
            advancing = true;
            try
            {
                NativeMatchSnapshot snapshot = match.Start();

                lastNowTicks = now.Ticks;
                started = true;
                return snapshot;
            }
            finally { advancing = false; }
        }
    }

    /// <summary>
    /// Uses the core's exact authentication capabilities, ordinals, and target-tick window. Clock backlog
    /// cannot enlarge client command windows.
    /// </summary>
    public NativeCommandResult Submit(NativePlayerBinding binding, NativePlayerCommand command)
    {
        lock (gate)
        {
            RequireNotAdvancing();
            return faulted ? NativeCommandResult.MatchNotRunning : match.Submit(binding, command);
        }
    }

    /// <summary>
    /// Revokes this driver's exact player capability and removes unexecuted commands. Repeated revocation
    /// is idempotent, preserving characters/scores.
    /// </summary>
    /// <returns>True for this driver's capabilities; false for null or another match's capability.</returns>
    /// <remarks>
    /// Trusted callers determine session, round, and endpoint invalidation. Cross-thread calls wait for the
    /// whole active advance batch, without rolling back committed ticks. Reject scene-callback mutation
    /// reentry. Revocation remains available after fault/completion and is never reversed.
    /// </remarks>
    public bool RevokePlayerBinding(NativePlayerBinding? binding)
    {
        lock (gate)
        {
            RequireNotAdvancing();
            return match.RevokePlayerBinding(binding);
        }
    }

    public NativeMatchSnapshot CaptureSnapshot()
    {
        lock (gate) return match.Capture();
    }

    public NativeMatchSnapshot Stop()
    {
        lock (gate)
        {
            RequireNotAdvancing();
            bufferedElapsedTicks = 0;
            return match.Stop();
        }
    }

    /// <summary>
    /// Nonnegative absolute monotonic time. Reject backward time, overflow, or excessive whole-tick backlog
    /// before advancing, leaving anchors, backlog, commands, and world unchanged. Callers must handle
    /// overload rather than truncate time and claim catch-up. Score/time-limit completion clears remaining
    /// time because no later game ticks exist.
    /// </summary>
    public NativeAuthoritativeAdvanceResult Advance(TimeSpan now)
    {
        ValidateNow(now);
        lock (gate)
        {
            RequireNotAdvancing();
            if (!started || faulted || match.Capture().Phase != NativeMatchPhase.Running)
                throw new InvalidOperationException("只有已启动且仍运行的权威时钟可以推进。");
            if (now.Ticks < lastNowTicks)
                throw new ArgumentOutOfRangeException(nameof(now), "服务端单调时钟不能倒退。");

            long elapsed = now.Ticks - lastNowTicks;

            if (elapsed > long.MaxValue - bufferedElapsedTicks)
                throw new ArgumentOutOfRangeException(nameof(now), "服务端待处理时间加法溢出。");

            long pending = bufferedElapsedTicks + elapsed;

            // Check by division so FixedTick near TimeSpan.MaxValue cannot overflow multiplication.
            if (pending / fixedTickTicks > MaximumBufferedTicks)
                throw new ArgumentOutOfRangeException(nameof(now), "服务端固定 tick 积压超过显式预算。");

            var completed = new List<NativeMatchTickResult>(MaximumTicksPerAdvance);

            lastNowTicks = now.Ticks;
            bufferedElapsedTicks = pending;
            advancing = true;
            try
            {
                while (bufferedElapsedTicks >= fixedTickTicks && completed.Count < MaximumTicksPerAdvance)
                {
                    NativeMatchTickResult tick = match.AdvanceTick();

                    completed.Add(tick);
                    bufferedElapsedTicks -= fixedTickTicks;
                    if (tick.Snapshot.Phase != NativeMatchPhase.Running)
                    {
                        bufferedElapsedTicks = 0;
                        break;
                    }
                }
                return new(match.Capture(), completed.AsReadOnly(), TimeSpan.FromTicks(bufferedElapsedTicks));
            }
            catch (Exception error)
            {
                faulted = true;
                bufferedElapsedTicks = 0;
                throw new NativeAuthoritativeDriverException(error, completed.AsReadOnly(), match.Capture());
            }
            finally { advancing = false; }
        }
    }

    private static void ValidateNow(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now), "服务端单调时间必须非负。");
    }

    private void RequireNotAdvancing()
    {
        if (advancing) throw new InvalidOperationException("场景或输入集合回调不能重入并修改正在操作的权威时钟。");
    }
}
