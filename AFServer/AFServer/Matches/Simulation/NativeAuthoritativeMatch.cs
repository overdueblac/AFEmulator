using System.Collections.ObjectModel;
using System.Numerics;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Distance is nearest distance along the ray/sphere-center path; Position is world contact and Normal a
/// unit outward normal.
/// </summary>
public readonly record struct NativeCollisionHit(float Distance, Vector3 Position, Vector3 Normal);

/// <summary>
/// Caller-provided validated immutable geometry. true must report nearest contact, including initial
/// contact.
/// </summary>
public interface INativeCollisionScene
{
    bool OverlapsSphere(Vector3 center, float radius);
    bool SweepSphere(Vector3 start, Vector3 end, float radius, out NativeCollisionHit hit);
    bool Raycast(Vector3 origin, Vector3 normalizedDirection, float maxDistance, out NativeCollisionHit hit);
}

/// <summary>
/// All values come from trusted server rules. Contains no game-default weapons, reload logic, or
/// client-supplied damage.
/// </summary>
public sealed record NativeAuthoritativeWeaponRules(int Damage, int SpawnAmmo, long FireIntervalTicks, float Range);

/// <summary>
/// An explicitly configured bounded model: constant-height planar movement, spherical collisions/hits, one
/// hitscan weapon, and team kill scores. Not UE3 Pawn physics, actual mode settings, or network RPC;
/// callers must recover/validate those inputs separately. Keep eye points/hit spheres inside player
/// collision spheres so a player behind a wall cannot fire from beyond it.
/// </summary>
public sealed record NativeAuthoritativeRules(
    TimeSpan FixedTick,
    float MoveSpeed,
    Vector3 MovementPlaneNormal,
    float PlayerRadius,
    Vector3 EyeOffset,
    Vector3 HitSphereOffset,
    float HitSphereRadius,
    int MaximumHealth,
    NativeAuthoritativeWeaponRules Weapon,
    long RespawnTicks,
    bool FriendlyFire,
    int EnemyKillScore,
    long WinningTeamScore,
    long MatchDurationTicks,
    int MaximumCommandLeadTicks);

/// <summary>
/// Intent only: clients cannot specify position, velocity, object identity, hit targets, or damage.
/// TargetTick is an upcoming server tick; Sequence increases strictly from 1. At most one command per
/// player per tick.
/// </summary>
public readonly record struct NativePlayerCommand(long Sequence, long TargetTick, Vector3 MoveDirection,
    Vector3 AimDirection, bool Fire);

/// <summary>
/// Owner capability that clients cannot construct. Callers authenticate and retain this instance in the
/// exact session, never finding another player's binding from an untrusted datagram PlayerId. When session
/// eligibility ends, call RevokePlayerBinding on its match. The object itself proves no authentication,
/// room, or endpoint association.
/// </summary>
public sealed class NativePlayerBinding
{
    internal NativePlayerBinding(string ownerIdentity, int playerId)
    {
        OwnerIdentity = ownerIdentity;
        PlayerId = playerId;
    }

    public string OwnerIdentity { get; }
    public int PlayerId { get; }
}

public enum NativeMatchPhase { Created, Running, Ended, Faulted }
public enum NativeMatchEndReason { ScoreLimit, TimeLimit, ServerStopped }
public enum NativeCommandResult
{
    Accepted, MatchNotRunning, InvalidOwner, Dead, InvalidSequence, OutOfOrder,
    TickExpired, TickTooFarAhead, TickOutOfOrder, InvalidDirection, CommandQueueFull
}
public enum NativeMatchEventKind { Shot, Damage, Killed, Respawned, Ended }
public readonly record struct NativeMatchEvent(long Tick, NativeMatchEventKind Kind, int PlayerId,
    int? OtherPlayerId, int Amount);
public sealed record NativePlayerSnapshot(int PlayerId, int TeamId, Vector3 Position, Vector3 AimDirection,
    int Health, int Ammo, long SpawnGeneration, long? RespawnAtTick, long NextFireTick,
    long LastAcceptedSequence, int PendingCommands, int Kills, int Deaths)
{
    public bool IsAlive => Health > 0;

    /// <summary>
    /// Whether command capability is irreversibly revoked, independent of character liveness, collision,
    /// and scores.
    /// </summary>
    public bool IsBindingRevoked { get; init; }
}
public sealed record NativeMatchSnapshot(NativeMatchPhase Phase, long Tick,
    IReadOnlyList<NativePlayerSnapshot> Players, IReadOnlyDictionary<int, long> TeamScores,
    NativeMatchEndReason? EndReason, int? WinningTeamId);
public sealed record NativeMatchTickResult(NativeMatchSnapshot Snapshot, IReadOnlyList<NativeMatchEvent> Events);

/// <summary>
/// Network-free authoritative state machine with serialized public operations. AdvanceTick advances exactly
/// one fixed tick, never client dt. Movement runs in PlayerId order with conservative whole-step blocking,
/// no wall sliding/gravity. Players alive in the firing phase fire simultaneously, then damage resolves by
/// PlayerId with one death per victim. Allies block shots; disabling friendly fire prevents damage, and
/// only enemy kills score. Death clears commands, revival resets health/ammo, and blocked spawns retry per
/// tick. Scene-query faults atomically reject the tick and fault the match, preventing partial
/// physics/combat publication. Mutation rejects same-thread scene/collection callback reentry; callbacks
/// may read committed snapshots. Cross-thread revocation awaits active work and removes only unexecuted
/// commands, never committed ticks.
/// </summary>
public sealed class NativeAuthoritativeMatch
{
    // Implementation resource limits, not native player-count, spawn-point, or command-window rules.
    public const int MaximumPlayers = 128;
    public const int MaximumSpawnPositionsPerPlayer = 256;
    public const int MaximumCommandLeadTicks = 256;
    public const int MaximumOwnerIdentityLength = 1024;

    private readonly object _gate = new();
    private readonly NativeAuthoritativeRules _rules;
    private readonly INativeCollisionScene _scene;
    private SortedDictionary<int, PlayerState> _players = new();
    private Dictionary<int, long> _scores = new();
    private NativeMatchPhase _phase = NativeMatchPhase.Created;
    private NativeMatchEndReason? _endReason;
    private int? _winner;
    private long _tick;
    private bool _mutationInProgress;
    private readonly float _stepDistance;

    public NativeAuthoritativeMatch(NativeAuthoritativeRules rules, INativeCollisionScene scene)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(scene);
        ValidateRules(rules);
        _rules = rules with { MovementPlaneNormal = ScaleToUnit(rules.MovementPlaneNormal, Length(rules.MovementPlaneNormal)) };
        _scene = scene;
        _stepDistance = (float)(rules.MoveSpeed * rules.FixedTick.TotalSeconds);
    }

    /// <summary>
    /// Server-only registration of authenticated identities before Start. Trusted spawn coordinates are
    /// copied.
    /// </summary>
    public NativePlayerBinding BindAuthenticatedPlayer(string authenticatedOwnerIdentity, int playerId, int teamId,
        IReadOnlyList<Vector3> spawnPositions)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwnerIdentity);
        ArgumentNullException.ThrowIfNull(spawnPositions);
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();
            int spawnCount = spawnPositions.Count;

            if (playerId <= 0 || teamId <= 0 || spawnCount is <= 0 or > MaximumSpawnPositionsPerPlayer ||
                authenticatedOwnerIdentity.Length > MaximumOwnerIdentityLength)
                throw new ArgumentException("玩家、队伍、拥有者身份和出生点必须有效且不超过实现资源上限。");
            ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedOwnerIdentity);

            if (_phase != NativeMatchPhase.Created) throw new InvalidOperationException("运行中的对局不能改变登记身份。");
            if (_players.Count >= MaximumPlayers) throw new InvalidOperationException("对局玩家数超过实现资源上限。");
            if (_players.ContainsKey(playerId) || _players.Values.Any(player =>
                StringComparer.Ordinal.Equals(player.Binding.OwnerIdentity, authenticatedOwnerIdentity)))
                throw new ArgumentException("玩家或拥有者身份重复。");

            // Validate count/roster before bounded allocation/indexed copying; never invoke potentially unbounded input enumerators.
            var positions = new Vector3[spawnCount];

            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = spawnPositions[i];
                if (!IsFinite(positions[i]))
                    throw new ArgumentException("出生点必须是有限坐标。", nameof(spawnPositions));
            }

            var binding = new NativePlayerBinding(authenticatedOwnerIdentity, playerId);

            _players.Add(playerId, new PlayerState(binding, teamId, positions));
            _scores.TryAdd(teamId, 0);
            return binding;
        }
    }

    public NativeMatchSnapshot Start()
    {
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();

            if (_phase != NativeMatchPhase.Created) throw new InvalidOperationException("对局只能启动一次。");
            if (_scores.Count < 2) throw new InvalidOperationException("该模型要求至少两个队伍。");

            SortedDictionary<int, PlayerState> next = ClonePlayers();

            foreach (PlayerState player in next.Values)
                if (!TrySpawn(player, next, 0))
                    throw new InvalidOperationException($"玩家 {player.Binding.PlayerId} 没有可用出生点；未启动任何玩家。");
            _players = next;
            _phase = NativeMatchPhase.Running;
            return CaptureUnsafe();
        }
    }

    public NativeCommandResult Submit(NativePlayerBinding binding, NativePlayerCommand command)
    {
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();

            if (_phase != NativeMatchPhase.Running) return NativeCommandResult.MatchNotRunning;
            if (binding is null || !_players.TryGetValue(binding.PlayerId, out PlayerState? player) ||
                !ReferenceEquals(player.Binding, binding) || player.BindingRevoked) return NativeCommandResult.InvalidOwner;
            if (player.Health == 0) return NativeCommandResult.Dead;
            if (command.Sequence <= 0 || command.Sequence <= player.LastSequence)
                return NativeCommandResult.InvalidSequence;
            if (player.LastSequence == long.MaxValue || command.Sequence != player.LastSequence + 1)
                return NativeCommandResult.OutOfOrder;
            if (command.TargetTick <= _tick) return NativeCommandResult.TickExpired;
            if (command.TargetTick - _tick > _rules.MaximumCommandLeadTicks)
                return NativeCommandResult.TickTooFarAhead;
            if (command.TargetTick <= player.LastTargetTick) return NativeCommandResult.TickOutOfOrder;
            if (!TryValidateDirections(command, out NativePlayerCommand normalized))
                return NativeCommandResult.InvalidDirection;
            // Sequence/tick windows already bound queues; check independently so later scheduling changes cannot break memory limits.
            if (player.Pending.Count >= _rules.MaximumCommandLeadTicks) return NativeCommandResult.CommandQueueFull;
            player.Pending.Add(normalized.TargetTick, normalized);
            player.LastSequence = normalized.Sequence;
            player.LastTargetTick = normalized.TargetTick;
            return NativeCommandResult.Accepted;
        }
    }

    /// <summary>
    /// Revokes future commands through this match's exact capability instance and removes all unexecuted
    /// commands for that player.
    /// </summary>
    /// <returns>
    /// True for this match's capabilities, including repeated revocation; false for null or another match's
    /// capability.
    /// </returns>
    /// <remarks>
    /// Revokes commands only, preserving characters, health, spawning, collision, scores, and teams; native
    /// disconnect rules remain unrecovered. Irreversible revocation retains identity/PlayerId, forbidding
    /// reregistration or reuse. Still allowed after completion/fault. Active operations finish first;
    /// same-thread callback mutation throws InvalidOperationException.
    /// </remarks>
    public bool RevokePlayerBinding(NativePlayerBinding? binding)
    {
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();

            if (binding is null || !_players.TryGetValue(binding.PlayerId, out PlayerState? player) ||
                !ReferenceEquals(player.Binding, binding)) return false;

            player.BindingRevoked = true;
            player.Pending.Clear();
            return true;
        }
    }

    public NativeMatchTickResult AdvanceTick()
    {
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();

            if (_phase != NativeMatchPhase.Running) throw new InvalidOperationException("只有运行中的对局可以推进。");

            long nextTick = _tick + 1;
            SortedDictionary<int, PlayerState> next = ClonePlayers();
            var scores = new Dictionary<int, long>(_scores);
            var events = new List<NativeMatchEvent>();

            try
            {
                foreach (PlayerState player in next.Values)
                    if (player.Health == 0 && player.RespawnAtTick <= nextTick && TrySpawn(player, next, nextTick))
                        events.Add(new(nextTick, NativeMatchEventKind.Respawned, player.Binding.PlayerId, null, 0));

                var fireCommands = new List<PlayerState>();

                foreach (PlayerState player in next.Values)
                {
                    if (!player.Pending.Remove(nextTick, out NativePlayerCommand command) || player.Health == 0) continue;
                    player.Aim = command.AimDirection;
                    MovePlayer(player, command.MoveDirection, next);
                    if (command.Fire && player.Ammo > 0 && nextTick >= player.NextFireTick)
                        fireCommands.Add(player);
                }

                var hits = new List<(PlayerState Shooter, PlayerState Target)>();

                foreach (PlayerState shooter in fireCommands)
                {
                    shooter.Ammo--;
                    shooter.NextFireTick = nextTick + _rules.Weapon.FireIntervalTicks;
                    events.Add(new(nextTick, NativeMatchEventKind.Shot, shooter.Binding.PlayerId, null, 0));

                    PlayerState? target = FindTarget(shooter, next);

                    if (target is not null && (_rules.FriendlyFire || target.TeamId != shooter.TeamId))
                        hits.Add((shooter, target));
                }

                foreach (var (shooter, target) in hits)
                {
                    if (target.Health == 0) continue;

                    int damage = Math.Min(target.Health, _rules.Weapon.Damage);

                    target.Health -= damage;
                    events.Add(new(nextTick, NativeMatchEventKind.Damage, shooter.Binding.PlayerId, target.Binding.PlayerId, damage));
                    if (target.Health != 0) continue;
                    target.Deaths = checked(target.Deaths + 1);
                    target.RespawnAtTick = nextTick + _rules.RespawnTicks;
                    target.Pending.Clear();
                    target.LastTargetTick = nextTick;
                    if (target.TeamId != shooter.TeamId)
                    {
                        shooter.Kills = checked(shooter.Kills + 1);
                        scores[shooter.TeamId] = checked(scores[shooter.TeamId] + _rules.EnemyKillScore);
                    }
                    events.Add(new(nextTick, NativeMatchEventKind.Killed, shooter.Binding.PlayerId, target.Binding.PlayerId, 0));
                }
            }
            catch
            {
                _phase = NativeMatchPhase.Faulted;
                foreach (PlayerState player in _players.Values) player.Pending.Clear();
                throw;
            }

            _players = next;
            _scores = scores;
            _tick = nextTick;
            if (scores.Values.Any(score => score >= _rules.WinningTeamScore)) EndUnsafe(NativeMatchEndReason.ScoreLimit);
            else if (_tick >= _rules.MatchDurationTicks) EndUnsafe(NativeMatchEndReason.TimeLimit);
            if (_phase == NativeMatchPhase.Ended)
                events.Add(new(nextTick, NativeMatchEventKind.Ended, 0, null, 0));
            return new(CaptureUnsafe(), events.AsReadOnly());
        }
    }

    public NativeMatchSnapshot Capture()
    {
        lock (_gate) return CaptureUnsafe();
    }

    public NativeMatchSnapshot Stop()
    {
        lock (_gate)
        {
            using MutationScope mutation = BeginMutation();

            if (_phase is NativeMatchPhase.Created or NativeMatchPhase.Running)
                EndUnsafe(NativeMatchEndReason.ServerStopped);
            return CaptureUnsafe();
        }
    }

    private void EndUnsafe(NativeMatchEndReason reason)
    {
        _phase = NativeMatchPhase.Ended;
        _endReason = reason;

        long highScore = _scores.Count == 0 ? 0 : _scores.Values.Max();
        int[] winners = _scores.Where(pair => pair.Value == highScore).Select(pair => pair.Key).ToArray();

        _winner = reason != NativeMatchEndReason.ServerStopped && winners.Length == 1 ? winners[0] : null;
        foreach (PlayerState player in _players.Values) player.Pending.Clear();
    }

    private bool TrySpawn(PlayerState player, SortedDictionary<int, PlayerState> players, long tick)
    {
        foreach (Vector3 position in player.Spawns)
        {
            if (_scene.OverlapsSphere(position, _rules.PlayerRadius) || players.Values.Any(other =>
                other != player && other.Health > 0 && Distance(position, other.Position) <= 2.0 * _rules.PlayerRadius)) continue;
            player.Position = position;
            // No fabricated initial facing: firing requires explicit valid aim in a command from this life.
            player.Aim = Vector3.Zero;
            player.Health = _rules.MaximumHealth;
            player.Ammo = _rules.Weapon.SpawnAmmo;
            player.NextFireTick = tick;
            player.RespawnAtTick = null;
            player.SpawnGeneration = checked(player.SpawnGeneration + 1);
            return true;
        }
        return false;
    }

    private void MovePlayer(PlayerState player, Vector3 move, SortedDictionary<int, PlayerState> players)
    {
        if (move == Vector3.Zero) return;

        Vector3 target = player.Position + move * _stepDistance;

        if (!IsFinite(target)) throw new InvalidOperationException("服务端移动坐标溢出。");

        double length = Distance(player.Position, target);

        if (length == 0) return;
        if (_scene.SweepSphere(player.Position, target, _rules.PlayerRadius, out NativeCollisionHit hit))
        {
            ValidateHit(hit, length);
            return;
        }
        if (_scene.OverlapsSphere(target, _rules.PlayerRadius)) return;

        Vector3 direction = UnitDirection(player.Position, target);

        foreach (PlayerState other in players.Values)
            if (other != player && other.Health > 0 && RaySphere(player.Position, direction, length,
                other.Position, 2.0 * _rules.PlayerRadius, out _)) return;
        player.Position = target;
    }

    private PlayerState? FindTarget(PlayerState shooter, SortedDictionary<int, PlayerState> players)
    {
        Vector3 origin = shooter.Position + _rules.EyeOffset;

        if (!IsFinite(origin)) throw new InvalidOperationException("服务端眼点坐标溢出。");

        double eyeDistance = Distance(shooter.Position, origin);

        if (eyeDistance > 0 && _scene.Raycast(shooter.Position, UnitDirection(shooter.Position, origin),
            (float)eyeDistance, out NativeCollisionHit eyeHit))
        {
            ValidateHit(eyeHit, eyeDistance);
            return null;
        }

        double wallDistance = double.PositiveInfinity;

        if (_scene.Raycast(origin, shooter.Aim, _rules.Weapon.Range, out NativeCollisionHit hit))
        {
            ValidateHit(hit, _rules.Weapon.Range);
            wallDistance = hit.Distance;
        }

        double bestDistance = _rules.Weapon.Range;
        PlayerState? nearest = null;

        foreach (PlayerState target in players.Values)
        {
            if (target == shooter || target.Health == 0) continue;

            Vector3 center = target.Position + _rules.HitSphereOffset;

            if (!IsFinite(center)) throw new InvalidOperationException("服务端命中球坐标溢出。");
            if (!RaySphere(origin, shooter.Aim, bestDistance, center, _rules.HitSphereRadius, out double distance)) continue;
            if (distance >= wallDistance || (nearest is not null && distance >= bestDistance)) continue;
            nearest = target;
            bestDistance = distance;
        }
        return nearest;
    }

    private bool TryValidateDirections(NativePlayerCommand command, out NativePlayerCommand normalized)
    {
        normalized = command;
        if (!IsFinite(command.MoveDirection) || !IsFinite(command.AimDirection)) return false;

        double movementLength = Length(command.MoveDirection);
        double aimLength = Length(command.AimDirection);

        if (movementLength > 1.000001 || aimLength <= 0 ||
            Math.Abs(Vector3.Dot(command.MoveDirection, _rules.MovementPlaneNormal)) > 0.000001) return false;

        // Project before limiting magnitude. Allow floating-point rounding, not accumulated vertical drift or diagonal acceleration.
        Vector3 move = command.MoveDirection - _rules.MovementPlaneNormal *
            Vector3.Dot(command.MoveDirection, _rules.MovementPlaneNormal);
        double projectedLength = Length(move);

        if (projectedLength > 1) move = ScaleToUnit(move, projectedLength);
        normalized = command with { MoveDirection = move, AimDirection = ScaleToUnit(command.AimDirection, aimLength) };
        return true;
    }

    private NativeMatchSnapshot CaptureUnsafe() => new(_phase, _tick,
        Array.AsReadOnly(_players.Values.Select(player => new NativePlayerSnapshot(player.Binding.PlayerId, player.TeamId,
            player.Position, player.Aim, player.Health, player.Ammo, player.SpawnGeneration, player.RespawnAtTick,
            player.NextFireTick, player.LastSequence, player.Pending.Count, player.Kills, player.Deaths)
            { IsBindingRevoked = player.BindingRevoked }).ToArray()),
        new ReadOnlyDictionary<int, long>(new Dictionary<int, long>(_scores)), _endReason, _winner);

    private SortedDictionary<int, PlayerState> ClonePlayers() => new(_players.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()));

    private MutationScope BeginMutation()
    {
        // Monitor permits same-thread reentry. Establish independent protection before scene/collection callbacks so work copies cannot overwrite revocation.
        if (_mutationInProgress) throw new InvalidOperationException("回调不能重入并修改正在操作的权威对局。");

        _mutationInProgress = true;
        return new(this);
    }

    private readonly struct MutationScope(NativeAuthoritativeMatch owner) : IDisposable
    {
        public void Dispose() => owner._mutationInProgress = false;
    }

    private static void ValidateRules(NativeAuthoritativeRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules.Weapon);

        double step = rules.MoveSpeed * rules.FixedTick.TotalSeconds;

        if (rules.FixedTick <= TimeSpan.Zero || !float.IsFinite(rules.MoveSpeed) || rules.MoveSpeed < 0 ||
            !double.IsFinite(step) || step > float.MaxValue || !IsFinite(rules.MovementPlaneNormal) ||
            Math.Abs(Length(rules.MovementPlaneNormal) - 1) > 0.000001 ||
            !float.IsFinite(rules.PlayerRadius) || rules.PlayerRadius <= 0 ||
            !IsFinite(rules.EyeOffset) || Length(rules.EyeOffset) > rules.PlayerRadius ||
            !IsFinite(rules.HitSphereOffset) || !float.IsFinite(rules.HitSphereRadius) || rules.HitSphereRadius <= 0 ||
            Length(rules.HitSphereOffset) + rules.HitSphereRadius > rules.PlayerRadius ||
            rules.MaximumHealth <= 0 || rules.Weapon.Damage <= 0 || rules.Weapon.SpawnAmmo < 0 ||
            rules.Weapon.FireIntervalTicks <= 0 || !float.IsFinite(rules.Weapon.Range) || rules.Weapon.Range <= 0 ||
            rules.RespawnTicks <= 0 || rules.EnemyKillScore <= 0 || rules.WinningTeamScore <= 0 ||
            rules.MatchDurationTicks <= 0 || rules.MaximumCommandLeadTicks is <= 0 or > MaximumCommandLeadTicks)
            throw new ArgumentException("权威规则缺失、非有限、越界或其眼点/命中球超出碰撞体。", nameof(rules));

        long reserve = Math.Max(Math.Max(rules.RespawnTicks, rules.Weapon.FireIntervalTicks), rules.MaximumCommandLeadTicks);

        if (rules.MatchDurationTicks > long.MaxValue - reserve)
            throw new ArgumentException("tick 时间范围将溢出。", nameof(rules));
    }

    private static void ValidateHit(NativeCollisionHit hit, double maximumDistance)
    {
        if (!float.IsFinite(hit.Distance) || hit.Distance < 0 || hit.Distance > maximumDistance + 0.0001 ||
            !IsFinite(hit.Position) || !IsFinite(hit.Normal) || Math.Abs(Length(hit.Normal) - 1) > 0.0001)
            throw new InvalidDataException("碰撞场景返回了无效接触；停止对局而不是越过遮挡。");
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static double Length(Vector3 value) => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
    private static double Distance(Vector3 left, Vector3 right)
    {
        double x = (double)left.X - right.X, y = (double)left.Y - right.Y, z = (double)left.Z - right.Z;

        return Math.Sqrt(x * x + y * y + z * z);
    }
    private static Vector3 ScaleToUnit(Vector3 value, double length) => new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length));
    private static Vector3 UnitDirection(Vector3 from, Vector3 to)
    {
        double length = Distance(from, to);

        return new((float)(((double)to.X - from.X) / length), (float)(((double)to.Y - from.Y) / length),
            (float)(((double)to.Z - from.Z) / length));
    }
    private static bool RaySphere(Vector3 origin, Vector3 direction, double maximumDistance, Vector3 center,
        double radius, out double distance)
    {
        double x = (double)origin.X - center.X, y = (double)origin.Y - center.Y, z = (double)origin.Z - center.Z;
        double c = x * x + y * y + z * z - radius * radius;

        if (c <= 0) { distance = 0; return true; }

        double a = (double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z;
        double b = x * direction.X + y * direction.Y + z * direction.Z;
        double discriminant = b * b - a * c;

        if (a == 0 || b >= 0 || discriminant < 0) { distance = 0; return false; }
        distance = (-b - Math.Sqrt(discriminant)) / a;
        return distance <= maximumDistance;
    }

    private sealed class PlayerState(NativePlayerBinding binding, int teamId, Vector3[] spawns)
    {
        internal NativePlayerBinding Binding { get; } = binding;
        internal int TeamId { get; } = teamId;
        internal Vector3[] Spawns { get; } = spawns;
        internal Vector3 Position;
        internal Vector3 Aim;
        internal int Health;
        internal int Ammo;
        internal long SpawnGeneration;
        internal long? RespawnAtTick;
        internal long NextFireTick;
        internal long LastSequence;
        internal long LastTargetTick;
        internal int Kills;
        internal int Deaths;
        internal bool BindingRevoked;
        internal SortedDictionary<long, NativePlayerCommand> Pending = new();

        internal PlayerState Clone()
        {
            var clone = (PlayerState)MemberwiseClone();

            clone.Pending = new(Pending);
            return clone;
        }
    }
}
