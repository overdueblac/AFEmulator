using System.Collections.Immutable;
using System.Diagnostics;
using AFConnect.Proxy;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    private readonly Dictionary<ulong, MatchStartOperation> startingMatches = [];
    private readonly Dictionary<ulong, MatchStartSnapshot> activeSnapshots = [];
    private readonly Dictionary<ulong, Task> releasingMatches = [];
    private bool matchServiceStopping;
    private bool afdevHealthSubscribed;

    private async ValueTask StartMatchAsync(GameSession session, GameRoom room, ushort responseCommand,
        CancellationToken cancellationToken, long receivedAt)
    {
        await RecoverFailedMatchAsync(room, cancellationToken);

        if (matchServiceStopping || room.MatchReleasePending)
        {
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);

            return;
        }
        if (startingMatches.TryGetValue(room.RoomId, out var pending))
        {
            if (allocator is IMatchPreparationAllocator)
            {
                await ReportPreparationAsync(pending, pending.Stage, cancellationToken);
            }
            protocolLog?.Invoke($"MATCH 房间={room.RoomId} 仍在准备，合并重复开赛请求。");

            return;
        }
        if (room.Started)
        {
            await SendAsync(session, responseCommand, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);

            return;
        }
        if (room.Members.Any(member => !member.Observer && member.Session != room.OwnerSession && member.State != 9))
        {
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);

            return;
        }

        var pendingReturns = returningSessions.Where(returning => !returning.Value.IsCompletedSuccessfully &&
            room.Members.Any(member => ReferenceEquals(returning.Key.Session, member.Session))).ToArray();

        if (pendingReturns.Length != 0)
        {
            foreach (var returning in pendingReturns)
            {
                if (returning.Value.IsCompleted && returning.Key.Session.MatchTransport is { } transport)
                    QueueReturningSession(returning.Key.Session, transport, returning.Key.AllocationId);
            }

            // Failure retries only the personal channel for the old grant. End this click without starting automatically after waiting or stopping other players in the old match.
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);
            return;
        }
        if (allocator is AfdevMatchAllocator afdev)
        {
            afdev.ReviveCoinStore = this;
            protocolLog?.Invoke($"MATCH 房间={room.RoomId} 开赛路由 mode=0x{room.ModeId:X4} map=0x{room.MapId:X4}，成员={room.Members.Count}。");

            if (!afdevHealthSubscribed)
            {
                afdev.AllocationFailed += OnAfdevAllocationFailed;
                afdevHealthSubscribed = true;
            }
            if (!AfdevMapCatalog.IsSupported(room.ModeId, room.MapId))
            {
                protocolLog?.Invoke($"MATCH 房间={room.RoomId} 拒绝未接入路由 mode=0x{room.ModeId:X4} map=0x{room.MapId:X4}；" +
                    "AFDEV 支持普通 BIO mode=0x0204、变异地带 map=0x004A、欢乐海岸 map=0x007A、" +
                    "恐怖之城 map=0x0027、老城区 map=0x0076、考古基地 map=0x003C；" +
                    "塔防战 mode=0x2005、联盟大厦 map=0x0060、沙漠神殿 map=0x008D；" +
                    "保卫战 mode=0x2002、钢铁森林 map=0x0010、星海虫影 map=0x005F；" +
                    "生存战 mode=0x2001、祭坛 map=0x002F、装配中心 map=0x0007；" +
                    "围剿战 mode=0x2003、藏匿之地 map=0x003F。");
                await RejectPreparationAsync(session, room, responseCommand, cancellationToken);

                return;
            }
            if (room.Members.Any(member => member.Session.MatchTransport is null))
            {
                protocolLog?.Invoke($"MATCH 房间={room.RoomId} 拒绝开赛：存在未绑定 AFDEV 认证通道的成员。");
                await RejectPreparationAsync(session, room, responseCommand, cancellationToken);

                return;
            }
        }

        // This is only a quick no-capacity rejection. ReserveAsync still decides exclusive binding atomically.
        if (allocator is AfdevMatchAllocator warmAllocator && warmAllocator.WarmAvailableCount == 0)
        {
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);
            return;
        }

        var timeout = allocator is IMatchPreparationAllocator preparation
            ? preparation.PreparationTimeout : TimeSpan.FromMinutes(10);
        var remaining = timeout - Stopwatch.GetElapsedTime(receivedAt);

        if (remaining <= TimeSpan.Zero)
        {
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);
            return;
        }

        using var snapshotDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var members = ImmutableArray.CreateBuilder<MatchStartMember>(room.Members.Count);

        snapshotDeadline.CancelAfter(remaining);
        try
        {
            foreach (var member in room.Members)
            {
                var document = await repository.LoadPlayerAsync(member.Session.Uin, snapshotDeadline.Token).AsTask().WaitAsync(snapshotDeadline.Token);
                var player = document is null ? PlayerState.Create(member.Session.Uin) : new PlayerState(member.Session.Uin, document);
                var equipment = player.Props.Where(prop => PlayerState.Number(prop, "gid") == player.CurrentRole ||
                    PlayerState.Number(prop, "gid") == player.CurrentBag ||
                    PlayerState.Number(prop, "owner_gid") == player.CurrentRole ||
                    PlayerState.Number(prop, "owner_gid") == player.CurrentBag)
                    .Select(prop => new MatchEquipmentSnapshot(PlayerState.Number(prop, "gid"),
                        checked((uint)PlayerState.Number(prop, "item_id")), PlayerState.Number(prop, "owner_gid"),
                        checked((byte)PlayerState.Number(prop, "location")))).ToImmutableArray();

                members.Add(new MatchStartMember(member.Session, member.SeatIndex, member.Observer,
                    new MatchPlayerSnapshot(player.Uin, player.Nickname, player.CurrentRole, player.CurrentBag, equipment)
                    {
                        ReviveCoinCount = member.Observer ? 0 : PveReviveCoins.Count(player)
                    }));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RejectPreparationAsync(session, room, responseCommand, cancellationToken);
            return;
        }

        var snapshot = new MatchStartSnapshot(Guid.NewGuid(), NativeMatchDescriptor.Capture(room), members.MoveToImmutable());

        if (allocator is AfdevMatchAllocator botAllocator)
        {
            try
            {
                snapshot = botAllocator.FreezeBotPlan(snapshot);
                timeout = botAllocator.GetPreparationTimeout(snapshot);
            }
            catch (InvalidOperationException failure)
            {
                protocolLog?.Invoke($"MATCH 房间={room.RoomId} Bot 计划拒绝：{failure.Message}");
                await RejectPreparationAsync(session, room, responseCommand, cancellationToken);
                return;
            }
        }

        var operation = new MatchStartOperation(room, snapshot, new MatchAdmissionRound(snapshot, timeProvider ?? TimeProvider.System),
            session, responseCommand, room.StartVersion, cancellationToken, receivedAt);

        startingMatches.Add(room.RoomId, operation);
        operation.Timeout = timeout;
        remaining = timeout - Stopwatch.GetElapsedTime(receivedAt);
        operation.Stopping.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        operation.Work = Task.Run(() => CompleteMatchStartAsync(operation));
        protocolLog?.Invoke($"MATCH 房间={room.RoomId} allocation={snapshot.AllocationId:N} 准备中，已冻结设置和精确成员。");
    }

    private async Task CompleteMatchStartAsync(MatchStartOperation operation)
    {
        Exception? failure = null;

        try
        {
            if (allocator is IMatchPreparationAllocator)
            {
                await dispatchGate.WaitAsync(operation.Stopping.Token);
                try
                {
                    CheckStartOperation(operation);
                    // Starting a warm instance preserves the client's existing connection overlay; A122 is reserved for completed requests.
                    await ReportPreparationAsync(operation, AfdevPreparationStage.Queued, operation.Stopping.Token);
                }
                finally { dispatchGate.Release(); }
                if (allocator is not AfdevMatchAllocator) operation.Progress = TrackPreparationAsync(operation);
            }

            var endpoint = allocator is IMatchSnapshotAllocator snapshots
                ? await snapshots.ReserveAsync(operation.Snapshot, operation.Stopping.Token)
                : await allocator.ReserveAsync(CopyLegacyRoom(operation), operation.Stopping.Token);

            if (endpoint.Key.Length != 16) throw new InvalidDataException("对局密钥必须为十六字节。");

            await StopPreparationProgressAsync(operation);
            await dispatchGate.WaitAsync(operation.Stopping.Token);
            try
            {
                CheckStartOperation(operation);
                await ReportPreparationAsync(operation, AfdevPreparationStage.Connecting, operation.Stopping.Token);
            }
            finally { dispatchGate.Release(); }

            foreach (var member in operation.Snapshot.Members)
            {
                MatchAdmissionTicket? ticket = null;

                await dispatchGate.WaitAsync(operation.Stopping.Token);

                try
                {
                    CheckStartOperation(operation);

                    if (member.Session.MatchTransport is not null) ticket = operation.Round.Issue(member.Session);
                }
                finally { dispatchGate.Release(); }

                MatchEndpoint localEndpoint;

                using (ticket)
                    localEndpoint = ticket is null ? endpoint : await member.Session.MatchTransport!.PrepareAsync(
                        operation.Snapshot, operation.Round, ticket, endpoint, operation.Stopping.Token);

                if (localEndpoint.Key.Length != 16) throw new InvalidDataException("连接端端点密钥布局无效。");

                operation.Endpoints.Add(member.Session, localEndpoint);
            }

            // Perform native reads outside the lobby lock. All member ACKs and checks share this click's time budget.
            if (allocator is AfdevMatchAllocator readyAllocator)
                await readyAllocator.GetReadyHost(operation.Snapshot.AllocationId).CheckReadyAsync(operation.Stopping.Token);

            await dispatchGate.WaitAsync(operation.Stopping.Token);

            try
            {
                CheckStartOperation(operation);
                RevokeMatchAdmission(operation.Room.RoomId);
                admissionRounds.Add(operation.Room.RoomId, operation.Round);
                activeSnapshots.Add(operation.Room.RoomId, operation.Snapshot);
                operation.Room.Endpoint = endpoint;
                operation.Room.Started = true;

                foreach (var member in operation.Room.Members) member.InMatch = true;
                foreach (var member in operation.Snapshot.Members) settlements.Remove(member.Session);

                // Revalidate the native world exclusively owned by the current allocation before publication; any failure revokes this grant.
                if (allocator is AfdevMatchAllocator afdev) _ = afdev.GetReadyHost(operation.Snapshot.AllocationId);
                CheckStartOperation(operation);
                operation.Round.Activate();
                await SendAsync(operation.Requester, operation.ResponseCommand,
                    new TdrWriter().U16(GameWire.ZoneSuccess), operation.Stopping.Token);
                CheckStartOperation(operation);

                if (operation.ResponseCommand == 0xA3A2)
                    await BroadcastAsync(operation.Room, 0xA3A1, new TdrWriter().U16(GameWire.ZoneSuccess),
                        operation.Stopping.Token, requireMatchPublication: true);

                foreach (var member in operation.Snapshot.Members)
                {
                    var local = operation.Endpoints[member.Session];

                    CheckStartOperation(operation);
                    operation.PublicationAttempted.Add(member.Session);
                    await SendAsync(member.Session, 0xA11A, new TdrWriter().U16(GameWire.ZoneSuccess)
                        .U32(GameWire.Address(local.Address)).U16(local.Port).Bytes(local.Key).String("", 128).U8(0),
                        operation.Stopping.Token);
                    operation.PublicationCompleted.Add(member.Session);

                    if (operation.PublicationCompleted.Count == 1 && allocator is AfdevMatchAllocator published)
                        published.GetReadyHost(operation.Snapshot.AllocationId).RecordEndpointPublished();

                    CheckStartOperation(operation);
                }

                protocolLog?.Invoke($"MATCH 房间={operation.Room.RoomId} allocation={operation.Snapshot.AllocationId:N} 原生及连接端就绪，A11A 已发布。");
                await ReportPreparationAsync(operation, AfdevPreparationStage.Completed, operation.Stopping.Token);
                startingMatches.Remove(operation.Room.RoomId);
            }
            finally { dispatchGate.Release(); }
        }
        catch (Exception error) { failure = error; }

        if (failure is not null)
        {
            await StopPreparationProgressAsync(operation);
            await dispatchGate.WaitAsync();

            try
            {
                operation.Round.RevokeAll();
                RevokeMatchAdmission(operation.Room.RoomId);
                activeSnapshots.Remove(operation.Room.RoomId);
                operation.Room.Started = false;
                operation.Room.Endpoint = null;
                operation.Room.MatchReleasePending = true;

                foreach (var member in operation.Room.Members) member.InMatch = false;

                using var notificationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));

                try { await ReportPreparationAsync(operation, AfdevPreparationStage.Cancelling, notificationDeadline.Token); }
                catch (Exception notification) { protocolLog?.Invoke($"准备结束通知失败：{notification.GetType().Name}。"); }
                try
                {
                    await ReportPreparationAsync(operation,
                        (operation.CancelRequested != 0 || operation.Room.StartVersion != operation.Version)
                            ? AfdevPreparationStage.Cancelled : AfdevPreparationStage.Failed, notificationDeadline.Token);
                }
                catch (Exception notification) { protocolLog?.Invoke($"准备终态通知失败：{notification.GetType().Name}。"); }

                if (!operation.Requester.IsDisconnected && operation.Requester.RoomId == operation.Room.RoomId &&
                    ReferenceEquals(rooms.GetValueOrDefault(operation.Room.RoomId), operation.Room))
                {
                    try
                    {
                        if (allocator is IMatchPreparationAllocator)
                            await SendAsync(operation.Requester, 0xA122, new TdrWriter().U32(0), notificationDeadline.Token);
                        await SendAsync(operation.Requester, operation.ResponseCommand, new TdrWriter().U16(0x8101), notificationDeadline.Token);
                    }
                    catch (Exception) { }
                }
            }
            finally { dispatchGate.Release(); }

            bool released = false;

            try
            {
                await ReleaseSnapshotResourcesAsync(operation.Snapshot);
                released = true;
            }
            catch (Exception cleanup)
            {
                failure = new AggregateException("开赛失败且资源仍待清理。", failure, cleanup);
            }

            await dispatchGate.WaitAsync();

            try
            {
                if (ReferenceEquals(startingMatches.GetValueOrDefault(operation.Room.RoomId), operation))
                    startingMatches.Remove(operation.Room.RoomId);

                operation.Room.MatchReleasePending = !released;

                if (!released) activeSnapshots[operation.Room.RoomId] = operation.Snapshot;
                if (released && operation.Room.Members.Count == 0) rooms.Remove(operation.Room.RoomId);

                protocolLog?.Invoke($"MATCH 房间={operation.Room.RoomId} allocation={operation.Snapshot.AllocationId:N} 开赛已撤销，A11A 尝试={operation.PublicationAttempted.Count} 完成={operation.PublicationCompleted.Count}：{failure.GetType().Name}: {failure.Message}");
            }
            finally { dispatchGate.Release(); }
        }

        operation.Stopping.Dispose();
        operation.ProgressStopping.Dispose();
    }

    private void CheckStartOperation(MatchStartOperation operation)
    {
        operation.Stopping.Token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(operation.StartedAt) >= operation.Timeout)
            throw new OperationCanceledException("本次开赛总预算已到期，禁止继续发布。", operation.Stopping.Token);

        if (operation.CancelRequested != 0 || matchServiceStopping || !ReferenceEquals(rooms.GetValueOrDefault(operation.Room.RoomId), operation.Room) ||
            !ReferenceEquals(startingMatches.GetValueOrDefault(operation.Room.RoomId), operation) ||
            operation.Room.StartVersion != operation.Version || operation.Room.Members.Count != operation.Snapshot.Members.Length ||
            operation.Snapshot.Members.Any(frozen => frozen.Session.IsDisconnected || frozen.Session.RoomId != operation.Room.RoomId ||
                !operation.Room.Members.Any(current => ReferenceEquals(current.Session, frozen.Session) &&
                    current.SeatIndex == frozen.SeatIndex && current.Observer == frozen.Observer)))
            throw new OperationCanceledException("开赛房间、设置版本或精确成员已经失效。");
    }

    private void InvalidateMatchStart(GameRoom room)
    {
        room.StartVersion++;

        if (!startingMatches.TryGetValue(room.RoomId, out var operation)) return;

        operation.Round.RevokeAll();
        operation.Stopping.Cancel();
    }

    private static GameRoom CopyLegacyRoom(MatchStartOperation operation)
    {
        var descriptor = operation.Snapshot.Descriptor;
        var room = new GameRoom
        {
            RoomId = descriptor.RoomId, OwnerUin = descriptor.OwnerUin, Name = operation.RoomName,
            ModeId = descriptor.ModeId, MapId = descriptor.MapId, MapString = descriptor.MapString,
            SubModeId = descriptor.SubModeId, RoomFlags = descriptor.RoomFlags,
            FighterCapacity = descriptor.FighterCapacity, ObserverCapacity = descriptor.ObserverCapacity,
            MatchSettingsWire = descriptor.MatchSettingsWire.ToArray()
        };

        foreach (var member in operation.Snapshot.Members) room.Members.Add(new RoomMember(member.Session, member.SeatIndex, member.Observer));

        return room;
    }

    private async Task ReleaseSnapshotResourcesAsync(MatchStartSnapshot snapshot)
    {
        foreach (var member in snapshot.Members)
        {
            var returningKey = (member.Session, snapshot.AllocationId);

            returningSessions.TryGetValue(returningKey, out var returning);
            if (returning is not null)
            {
                try { await returning; }
                catch (Exception) { /* The failed task retains its reservation; retry adapter cleanup below with the same allocation. */ }
            }
            if (member.Session.MatchTransport is { } transport)
                await transport.RevokeAsync(snapshot.AllocationId, CancellationToken.None);
            if (returning is not null)
                returningSessions.TryRemove(new KeyValuePair<(GameSession, Guid), Task>(returningKey, returning));
        }

        await allocator.ReleaseAsync(snapshot.RoomId, CancellationToken.None);
    }

    private async Task CompleteRoundReleaseAsync(GameRoom room, MatchStartSnapshot? snapshot)
    {
        Exception? failure = null;

        try
        {
            if (snapshot is not null) await ReleaseSnapshotResourcesAsync(snapshot);
            else await allocator.ReleaseAsync(room.RoomId, CancellationToken.None);
        }
        catch (Exception error) { failure = error; }

        await dispatchGate.WaitAsync();

        try
        {
            releasingMatches.Remove(room.RoomId);
            room.MatchReleasePending = failure is not null;

            if (failure is null)
            {
                activeSnapshots.Remove(room.RoomId);

                if (room.Members.Count == 0) rooms.Remove(room.RoomId);
            }
            else protocolLog?.Invoke($"MATCH 房间={room.RoomId} 回收未完成，保留实例与槽：{failure.GetType().Name}。");
        }
        finally { dispatchGate.Release(); }
    }

    private void OnAfdevAllocationFailed(Guid allocationId) => _ = HandleAfdevFailureAsync(allocationId);

    private async Task HandleAfdevFailureAsync(Guid allocationId)
    {
        await dispatchGate.WaitAsync();

        try
        {
            var operation = startingMatches.Values.FirstOrDefault(value => value.Snapshot.AllocationId == allocationId);

            if (operation is not null) InvalidateMatchStart(operation.Room);

            var snapshot = activeSnapshots.Values.FirstOrDefault(value => value.AllocationId == allocationId);

            if (snapshot is not null && rooms.TryGetValue(snapshot.RoomId, out var room))
                await ReleaseRoundAsync(room, preserveReadiness: true, CancellationToken.None);
        }
        catch (Exception error) { protocolLog?.Invoke($"MATCH 健康故障回收失败：{error.GetType().Name}。"); }
        finally { dispatchGate.Release(); }
    }

    /// <summary>
    /// On composition-root shutdown, cancel startup and revoke all grants before awaiting outside-lock
    /// network and process cleanup. Retain entry points for failed cleanup.
    /// </summary>
    public async ValueTask StopMatchesAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending;

        await dispatchGate.WaitAsync();

        try
        {
            matchServiceStopping = true;

            foreach (var room in rooms.Values.ToArray())
            {
                InvalidateMatchStart(room);

                if (!startingMatches.ContainsKey(room.RoomId))
                    await ReleaseRoundAsync(room, preserveReadiness: false, CancellationToken.None);
            }

            pending = startingMatches.Values.Select(operation => operation.Work!).Concat(releasingMatches.Values).ToArray();
        }
        finally { dispatchGate.Release(); }

        await Task.WhenAll(pending);

        await dispatchGate.WaitAsync();

        try
        {
            if (rooms.Values.Any(room => room.MatchReleasePending))
                throw new IOException("大厅仍保留未完整释放的对局资源；必须重试清理后才能关闭分配器。");
        }
        finally { dispatchGate.Release(); }
    }

    private sealed class MatchStartOperation(GameRoom room, MatchStartSnapshot snapshot, MatchAdmissionRound round,
        GameSession requester, ushort responseCommand, long version, CancellationToken cancellationToken, long receivedAt)
    {
        internal GameRoom Room { get; } = room;
        internal string RoomName { get; } = room.Name;
        internal MatchStartSnapshot Snapshot { get; } = snapshot;
        internal MatchAdmissionRound Round { get; } = round;
        internal GameSession Requester { get; } = requester;
        internal ushort ResponseCommand { get; } = responseCommand;
        internal long Version { get; } = version;
        internal CancellationTokenSource Stopping { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        internal Dictionary<GameSession, MatchEndpoint> Endpoints { get; } = [];
        internal HashSet<GameSession> PreparationFinished { get; } = [];
        internal HashSet<GameSession> PublicationAttempted { get; } = [];
        internal HashSet<GameSession> PublicationCompleted { get; } = [];
        internal Task? Work;
        internal Task Progress = Task.CompletedTask;
        internal CancellationTokenSource ProgressStopping { get; } = new();
        internal long StartedAt { get; } = receivedAt;
        internal TimeSpan Timeout = TimeSpan.FromMinutes(10);
        internal AfdevPreparationStage Stage = AfdevPreparationStage.Queued;
        internal int CancelRequested;
    }
}
