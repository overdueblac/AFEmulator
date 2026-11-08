using System.Collections.Concurrent;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    private readonly ConcurrentDictionary<(GameSession Session, Guid AllocationId), Task> returningSessions = new();

    /// <summary>
    /// Reregisters cleanup for an empty old room under the lobby lock using the original snapshot. Only
    /// queues cleanup; never waits for outside-lock resource disposal.
    /// </summary>
    private async ValueTask RetryAbandonedMatchReleasesAsync()
    {
        foreach (var room in rooms.Values.Where(room => room.Members.Count == 0 && room.MatchReleasePending &&
            !startingMatches.ContainsKey(room.RoomId) && !releasingMatches.ContainsKey(room.RoomId)).ToArray())
            await ReleaseRoundAsync(room, preserveReadiness: false, CancellationToken.None);
    }

    private async ValueTask<bool> HandleRoomAsync(GameSession session, PlayerState player, ushort command, byte[] body, CancellationToken cancellationToken, long receivedAt)
    {
        var reader = new TdrReader(body);

        switch (command)
        {
            case 0xA100:
                var mode = reader.U32(); var map = reader.U16(); reader.U16(); reader.U8();

                var start = reader.U16(); var count = reader.U16(); reader.End();

                var matching = rooms.Values.Where(r => r.Members.Count != 0 && !r.Started && !r.MatchReleasePending &&
                    !startingMatches.ContainsKey(r.RoomId) &&
                    (mode == 0 || r.ModeId == mode) && (map == 0 || r.MapId == map))
                    .OrderBy(r => r.DisplayId).ToArray();
                var page = matching.Skip(start).Take(count == 0 ? 20 : Math.Clamp((int)count, 1, 20)).ToArray();
                var list = new TdrWriter().U16(GameWire.ZoneSuccess).U16(matching.Length).U16(page.Length);

                foreach (var row in page) WriteBasicRoom(list, row);
                await SendAsync(session, 0xA102, list, cancellationToken);
                return true;
            case 0xA10A:
                await CreateRoomAsync(session, body, cancellationToken);
                return true;
            case 0xA104:
                await EnterRoomAsync(session, body, cancellationToken);
                return true;
            case 0xA107:
                var reason = reader.Remaining >= 2 ? reader.U16() : (ushort)0;

                await LeaveRoomAsync(session, reason, cancellationToken);
                await SendAsync(session, 0xA108, new TdrWriter().U16(GameWire.ZoneSuccess).U16(reason).String(""), cancellationToken);
                return true;
            case 0xA10D:
                await ChangeCampAsync(session, reader.U8(), cancellationToken); reader.End();
                return true;
            case 0xA110:
                await SetReadyAsync(session, body.Length == 0 || reader.U8() != 0, cancellationToken);
                return true;
            case 0xA11E:
                var settingsRoom = RequireRoom(session, true);

                await RecoverFailedMatchAsync(settingsRoom, cancellationToken);
                if (settingsRoom.Started) throw new InvalidOperationException("对局进行中不能更改游戏设置。");

                var settings = ReadSettings(reader, body);

                reader.End();
                InvalidateMatchStart(settingsRoom);

                bool layoutChanged = AfdevMapCatalog.UsesTeamSeats(settingsRoom.ModeId) != AfdevMapCatalog.UsesTeamSeats(settings.ModeId) ||
                    settings.ModeId is 0x1001 or 0x1004 && settingsRoom.FighterCapacity > 16;

                ApplySettings(settingsRoom, settings);
                if (layoutChanged)
                    foreach (var remaining in settingsRoom.Members.ToArray())
                        await remaining.Session.SendAsync(EnterPacket(settingsRoom), cancellationToken);
                return true;
            case 0xA113:
                await StartMatchAsync(session, RequireRoom(session, true), 0xA114, cancellationToken, receivedAt);
                return true;
            case 0xA11C:
                var joinedRoom = RequireRoom(session);

                if (!joinedRoom.Started) throw new InvalidOperationException("房间尚未开始对局。");

                var joined = joinedRoom.Members.Single(m => m.Session == session);

                joined.InMatch = true;
                await BroadcastAsync(joinedRoom, 0xA11D, new TdrWriter().U16(joined.SeatIndex), cancellationToken);
                return true;
            case 0xA117:
                // C2ZN_ReqQuitMatch contains only one Padding byte; do not interpret it as an exit reason or match result.
                reader.U8();
                reader.End();
                await QuitMatchAsync(session, player, cancellationToken);
                return true;
            case 0xA3A0:
                await AllocateMatchAsync(session, body, cancellationToken, receivedAt);
                return true;
            default:
                return false;
        }
    }

    private GameRoom RequireRoom(GameSession session, bool owner = false)
    {
        if (session.RoomId is not { } id || !rooms.TryGetValue(id, out var room) || !room.Members.Any(m => m.Session == session))
            throw new InvalidOperationException("玩家不在房间中。");
        if (owner && room.OwnerSession != session) throw new InvalidOperationException("该操作仅允许房主执行。");
        return room;
    }

    private static RoomSettings ReadSettings(TdrReader reader, byte[] body)
    {
        var offset = reader.Position;
        var modeId = reader.U32();
        var mapId = reader.U16();
        var mapString = reader.String(32);
        var subModeId = reader.U32();
        var roomFlags = reader.U32();

        reader.Read(10);
        // Preserve settings fields with unknown semantics verbatim to avoid losing mode-specific flags during reencoding.
        return new RoomSettings(modeId, mapId, mapString, subModeId, roomFlags,
            body.AsSpan(offset, reader.Position - offset).ToArray());
    }

    private static void ApplySettings(GameRoom room, RoomSettings settings)
    {
        byte fighterCapacity = settings.ModeId is 0x1001 or 0x1004 ? Math.Min(room.FighterCapacity, (byte)16) : room.FighterCapacity;

        if (AfdevMapCatalog.UsesTeamSeats(settings.ModeId))
            TeamDeathmatchSeats.Validate(fighterCapacity, room.ObserverCapacity);

        var reassigned = new List<(RoomMember Member, ushort Seat)>();

        if (AfdevMapCatalog.UsesTeamSeats(room.ModeId) != AfdevMapCatalog.UsesTeamSeats(settings.ModeId) ||
            fighterCapacity != room.FighterCapacity)
        {
            int oldPerCamp = room.FighterCapacity / 2;
            int perCamp = fighterCapacity / 2;
            var taken = new HashSet<int>();

            foreach (var member in room.Members.OrderBy(member => member.SeatIndex))
            {
                byte camp = AfdevMapCatalog.UsesTeamSeats(room.ModeId) ? TeamDeathmatchSeats.Camp(member.SeatIndex)
                    : member.SeatIndex < oldPerCamp ? (byte)1 : (byte)0;
                IEnumerable<int> candidates = member.Observer
                    ? AfdevMapCatalog.UsesTeamSeats(settings.ModeId)
                        ? TeamDeathmatchSeats.Observers(fighterCapacity, room.ObserverCapacity)
                        : Enumerable.Range(fighterCapacity, room.ObserverCapacity)
                    : AfdevMapCatalog.UsesTeamSeats(settings.ModeId)
                        ? Enumerable.Range(camp == 1 ? 0 : 16, perCamp)
                        : Enumerable.Range(0, fighterCapacity);
                int seat = candidates.FirstOrDefault(index => !taken.Contains(index), -1);

                if (seat < 0) throw new InvalidOperationException("切换模式后的队伍或观察席容量不足。");

                taken.Add(seat);
                reassigned.Add((member, (ushort)seat));
            }
        }

        // The caller must validate the entire request first. Commit only under the shared business lock so malformed packets cannot leave partial room settings.
        room.ModeId = settings.ModeId;
        room.FighterCapacity = fighterCapacity;
        room.MapId = settings.MapId;
        room.MapString = settings.MapString;
        room.SubModeId = settings.SubModeId;
        room.RoomFlags = settings.RoomFlags;
        room.MatchSettingsWire = settings.Wire;
        foreach (var (member, seat) in reassigned) member.SeatIndex = seat;
    }

    private async ValueTask CreateRoomAsync(GameSession session, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var id = nextRoomId;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var room = new GameRoom { RoomId = id, CreatedAtUtc = now, DisplayId = (ushort)(id & 0xFFFF),
            OwnerUin = session.Uin, OwnerSession = session, Name = reader.String(64) };
        var settings = ReadSettings(reader, body);

        room.FighterCapacity = reader.U8(); room.ObserverCapacity = reader.U8(); room.Password = reader.String(8);
        if (room.FighterCapacity == 0 || room.FighterCapacity + room.ObserverCapacity > 255)
            throw new InvalidDataException("房间容量无效。");
        // The trailing GameServerInfo is a client proposal, not a server allocation or connection destination.
        if (reader.Remaining != 0)
        {
            reader.U32(); reader.U16(); reader.Read(16); reader.String(128); reader.U8(); reader.End();
        }
        ApplySettings(room, settings);
        // The PH client may resend A10A before the first A105 arrives. When the same owner repeats identical room-creation fields within a short window,
        // resend only the response for an unstarted room. Different settings or a member creating a room indicate a new creation intent.
        // A new creation request after that window means the client returned to the room list; release stale membership using normal leave-room rules.
        // Validate the complete new request first so a malformed packet cannot clear a valid room.
        if (session.RoomId is { } previousId && rooms.TryGetValue(previousId, out var previous) &&
            previous.Members.Any(member => member.Session == session) &&
            previous.OwnerSession == session && !previous.Started &&
            now - previous.CreatedAtUtc < TimeSpan.FromSeconds(8) &&
            previous.Name == room.Name && previous.Password == room.Password &&
            previous.FighterCapacity == room.FighterCapacity && previous.ObserverCapacity == room.ObserverCapacity &&
            previous.MatchSettingsWire.AsSpan().SequenceEqual(room.MatchSettingsWire))
        {
            await SendAsync(session, 0xA10B, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
            await session.SendAsync(EnterPacket(previous), cancellationToken);
            return;
        }
        if (session.RoomId is not null) await LeaveRoomAsync(session, 0, cancellationToken);
        nextRoomId++;
        room.Members.Add(new RoomMember(session, 0, false));
        rooms.Add(id, room); session.RoomId = id;
        try
        {
            await SendAsync(session, 0xA10B, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
            await session.SendAsync(EnterPacket(room), cancellationToken);
            await SendAsync(session, 0xA106, WriteMember(new TdrWriter(), room, room.Members[0]), cancellationToken);
        }
        catch
        {
            // The new room has no other members or DS allocation. A downstream failure must not leave an invisible placeholder room.
            rooms.Remove(id);
            session.RoomId = null;
            throw;
        }
    }

    private async ValueTask EnterRoomAsync(GameSession session, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var id = reader.U64();
        var password = reader.Remaining >= 4 ? reader.String(8) : "";
        var observer = reader.Remaining == 1 && reader.U8() != 0;

        reader.End();
        if (!rooms.TryGetValue(id, out var room) || room.Members.Count == 0 || room.Started || room.MatchReleasePending ||
            startingMatches.ContainsKey(id) ||
            (session.RoomId is not null && session.RoomId != id)
            || room.Password != password)
        {
            // No failure MatchRoomInfo layout has been verified. Log and reject the connection rather than fabricate a decodable success packet.
            throw new InvalidOperationException("房间不存在、已开始、密码错误或玩家已在其他房间。");
        }

        var existing = room.Members.FirstOrDefault(m => m.Session == session);

        if (existing is not null) { await session.SendAsync(EnterPacket(room), cancellationToken); return; }

        var first = observer ? room.FighterCapacity : 0;
        var capacity = observer ? room.ObserverCapacity : room.FighterCapacity;
        var seats = !AfdevMapCatalog.UsesTeamSeats(room.ModeId) ? Enumerable.Range(first, capacity) : observer
            ? TeamDeathmatchSeats.Observers(room.FighterCapacity, room.ObserverCapacity)
            : TeamDeathmatchSeats.Fighters(room.FighterCapacity);
        var seat = seats.FirstOrDefault(index => room.Members.All(m => m.SeatIndex != index), -1);

        if (seat < 0) throw new InvalidOperationException("房间没有可用席位。");

        var member = new RoomMember(session, (ushort)seat, observer);

        InvalidateMatchStart(room);
        room.Members.Add(member); session.RoomId = id;
        try { await session.SendAsync(EnterPacket(room), cancellationToken); }
        catch { room.Members.Remove(member); session.RoomId = null; throw; }
        await BroadcastAsync(room, 0xA106, WriteMember(new TdrWriter(), room, member), cancellationToken, session);
    }

    private async ValueTask LeaveRoomAsync(GameSession session, ushort reason, CancellationToken cancellationToken)
    {
        if (session.RoomId is not { } id || !rooms.TryGetValue(id, out var room)) { session.RoomId = null; return; }

        var member = room.Members.FirstOrDefault(m => m.Session == session);

        if (member is null) { session.RoomId = null; return; }
        InvalidateMatchStart(room);
        admissionRounds.GetValueOrDefault(id)?.Revoke(session);
        if (activeSnapshots.TryGetValue(id, out var active) && session.MatchTransport is { } transport)
            QueueReturningSession(session, transport, active.AllocationId);

        room.Members.Remove(member); session.RoomId = null;
        if (room.Members.Count == 0)
        {
            if (startingMatches.ContainsKey(id)) return;

            // Retain an unjoinable empty-room context when cleanup fails. Later ZONE requests requeue cleanup, preserving the sole release entry point.
            await ReleaseRoundAsync(room, preserveReadiness: false, cancellationToken);
            if (!room.MatchReleasePending) rooms.Remove(id);
            return;
        }

        var ownerChanged = room.OwnerSession == session;

        if (ownerChanged)
        {
            room.OwnerSession = room.Members.OrderBy(m => m.SeatIndex).First().Session;
            room.OwnerUin = room.OwnerSession.Uin;
        }
        if (room.Started && room.Members.All(m => m.Observer || !m.InMatch)) await EndRoundAsync(room, cancellationToken);
        await BroadcastAsync(room, 0xA109, new TdrWriter().U16(member.SeatIndex).U16(reason), cancellationToken);
        if (ownerChanged)
        {
            // A105 is a verified complete room snapshot used to synchronize the new-owner flag without inventing a notification command.
            foreach (var remaining in room.Members.ToArray()) await remaining.Session.SendAsync(EnterPacket(room), cancellationToken);
        }
    }

    private async ValueTask ChangeCampAsync(GameSession session, byte camp, CancellationToken cancellationToken)
    {
        var room = RequireRoom(session);
        var member = room.Members.Single(m => m.Session == session);

        if (room.Started || member.Observer || camp > 1 || room.FighterCapacity < 2 || room.FighterCapacity % 2 != 0)
        {
            await SendAsync(session, 0xA10E, new TdrWriter().U16(0x8101), cancellationToken);
            return;
        }

        var oldSeat = member.SeatIndex;
        var perCamp = room.FighterCapacity / 2;
        var targetBase = camp == 0 ? (AfdevMapCatalog.UsesTeamSeats(room.ModeId) ? 16 : perCamp) : 0;
        var target = targetBase + (AfdevMapCatalog.UsesTeamSeats(room.ModeId) ? oldSeat % 16 : oldSeat) % perCamp;

        if (room.Members.Any(m => m != member && m.SeatIndex == target))
            target = Enumerable.Range(targetBase, perCamp).FirstOrDefault(index => room.Members.All(m => m.SeatIndex != index), -1);
        if (target < 0)
        {
            await SendAsync(session, 0xA10E, new TdrWriter().U16(0x8101), cancellationToken);
            return;
        }
        member.SeatIndex = (ushort)target;
        InvalidateMatchStart(room);
        await SendAsync(session, 0xA10E, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
        if (oldSeat != target) await BroadcastAsync(room, 0xA10F, new TdrWriter().U16(oldSeat).U16(target), cancellationToken);
    }

    private async ValueTask SetReadyAsync(GameSession session, bool ready, CancellationToken cancellationToken)
    {
        var room = RequireRoom(session);

        await RecoverFailedMatchAsync(room, cancellationToken);
        // The client may omit A117. Clicking Ready again indicates that the player has returned to the room interface.
        if (room.Started) await ReturnFromMatchAsync(room, session, cancellationToken);

        var member = room.Members.Single(m => m.Session == session);

        InvalidateMatchStart(room);
        member.State = ready ? 9U : 8U;
        await SendAsync(session, 0xA111, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
        if (ready) await BroadcastAsync(room, 0xA112, new TdrWriter().U16(member.SeatIndex), cancellationToken);
    }

    private async ValueTask AllocateMatchAsync(GameSession session, byte[] body, CancellationToken cancellationToken, long receivedAt)
    {
        var reader = new TdrReader(body);
        var requestedUin = reader.U64();

        if (requestedUin != 0 && requestedUin != session.Uin) throw new InvalidOperationException("匹配请求身份与会话不一致。");

        var settings = reader.Read(32).ToArray(); reader.U8(); var mapCount = reader.U8();

        for (var i = 0; i < mapCount; i++) reader.U32(); reader.End();

        var room = session.RoomId is not null ? RequireRoom(session, true) : null;

        if (room is null)
        {
            var id = nextRoomId++;

            room = new GameRoom { RoomId = id, CreatedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow(),
                DisplayId = (ushort)(id & 0xFFFF), OwnerUin = session.Uin, OwnerSession = session,
                Name = "Match", FighterCapacity = 4, ObserverCapacity = 0 };

            var settingsReader = new TdrReader(settings);
            var parsedSettings = ReadSettings(settingsReader, settings);

            // A3A0 carries a fixed 32-byte settings block. Preserve its existing trailing padding rather than applying A11E's full-packet consumption check.
            ApplySettings(room, parsedSettings);
            room.Members.Add(new RoomMember(session, 0, false));
            rooms.Add(id, room); session.RoomId = id;
        }
        await StartMatchAsync(session, room, 0xA3A2, cancellationToken, receivedAt);
    }

    private async ValueTask ReturnFromMatchAsync(GameRoom room, GameSession session, CancellationToken cancellationToken)
    {
        var member = room.Members.Single(m => m.Session == session);

        InvalidateMatchStart(room);
        admissionRounds.GetValueOrDefault(room.RoomId)?.Revoke(session);
        if (activeSnapshots.TryGetValue(room.RoomId, out var active) && session.MatchTransport is { } transport)
            QueueReturningSession(session, transport, active.AllocationId);

        member.InMatch = false; member.State = 8;
        if (room.Started && room.Members.All(m => m.Observer || !m.InMatch)) await EndRoundAsync(room, cancellationToken);
    }

    // A single player's departure drains only their exact channel. Whole-match release joins the same cleanup without blocking other players.
    private void QueueReturningSession(GameSession session, IMatchSessionTransport transport, Guid allocationId)
    {
        var key = (session, allocationId);

        if (returningSessions.TryGetValue(key, out var prior) && !prior.IsCompleted) return;

        var task = Task.Run(async () =>
        {
            try { await transport.RevokeAsync(allocationId, CancellationToken.None); }
            catch (Exception error)
            {
                protocolLog?.Invoke($"MATCH session={session.SessionId:N} allocation={allocationId:N} 单人通道回收待重试：{error.GetType().Name}。");
                throw;
            }
        });

        returningSessions[key] = task;
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsCompletedSuccessfully)
                returningSessions.TryRemove(new KeyValuePair<(GameSession, Guid), Task>(key, completed));
            else _ = completed.Exception;
        }, TaskScheduler.Default);
    }

    private ValueTask EndRoundAsync(GameRoom room, CancellationToken cancellationToken)
        => ReleaseRoundAsync(room, preserveReadiness: false, cancellationToken);

    private async ValueTask RecoverFailedMatchAsync(GameRoom room, CancellationToken cancellationToken)
    {
        if (!room.MatchReleasePending && (!room.Started || allocator is not IMatchServerHealthMonitor monitor ||
            !await monitor.HasFailedAsync(room.RoomId, cancellationToken))) return;
        // Deferred startup failure differs from normal completion: retain previous member-ready states so the same start intent can be retried.
        await ReleaseRoundAsync(room, preserveReadiness: true, cancellationToken);
        if (room.Members.Count == 0 && !room.MatchReleasePending) rooms.Remove(room.RoomId);
        protocolLog?.Invoke($"MATCH 房间={room.RoomId} 已清理失败对局，允许修改设置或重新开赛。");
    }

    private ValueTask ReleaseRoundAsync(GameRoom room, bool preserveReadiness, CancellationToken cancellationToken)
    {
        InvalidateMatchStart(room);
        RevokeMatchAdmission(room.RoomId);
        room.MatchReleasePending = true;
        room.Started = false; room.Endpoint = null;
        foreach (var member in room.Members)
        {
            member.InMatch = false;
            if (!preserveReadiness) member.State = 8;
        }
        if (!startingMatches.ContainsKey(room.RoomId) && !releasingMatches.ContainsKey(room.RoomId))
        {
            var snapshot = activeSnapshots.GetValueOrDefault(room.RoomId);

            releasingMatches.Add(room.RoomId, Task.Run(() => CompleteRoundReleaseAsync(room, snapshot)));
        }

        return ValueTask.CompletedTask;
    }

    private static void CheckMatchPublication(GameRoom room, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (room.Members.Any(member => member.Session.IsDisconnected))
            throw new IOException("开赛成员已断开，不能继续发布本回合端点。");
    }

    private static TdrWriter WriteMember(TdrWriter writer, GameRoom room, RoomMember member) => writer.U64(member.Session.Uin)
        .String(member.Session.Nickname, 32).U32(member.State).U16(member.Session == room.OwnerSession ? 4 : 0).U16(member.SeatIndex).U32(0);

    private static TdrWriter WriteCapacities(TdrWriter writer, GameRoom room) => writer.U8(room.FighterCapacity).U8(room.ObserverCapacity)
        .U8(room.Members.Count(m => !m.Observer)).U8(room.Members.Count(m => m.Observer));

    private static void WriteBasicRoom(TdrWriter writer, GameRoom room)
    {
        writer.U64(room.RoomId).U16(room.DisplayId).U64(0).String(room.Name, 64)
            .String(room.Members.First(m => m.Session == room.OwnerSession).Session.Nickname, 32).Bytes(room.MatchSettingsWire);
        WriteCapacities(writer, room);
    }

    private static byte[] EnterPacket(GameRoom room)
    {
        var writer = new TdrWriter().U16(GameWire.ZoneSuccess).U64(room.RoomId).U16(room.DisplayId).U64(0).U32(1)
            .String(room.Name, 64).Bytes(room.MatchSettingsWire);

        WriteCapacities(writer, room).U8(room.Members.Count);
        foreach (var member in room.Members.OrderBy(m => m.SeatIndex)) WriteMember(writer, room, member);
        return GameWire.Packet(0xA105, writer);
    }

    private static async ValueTask BroadcastAsync(GameRoom room, ushort command, TdrWriter body, CancellationToken cancellationToken,
        GameSession? exclude = null, bool requireMatchPublication = false)
    {
        var packet = GameWire.Packet(command, body);

        foreach (var member in room.Members.Where(m => m.Session != exclude).ToArray())
        {
            if (requireMatchPublication) CheckMatchPublication(room, cancellationToken);
            try { await member.Session.SendAsync(packet, cancellationToken); }
            catch (IOException) when (!requireMatchPublication)
            { /* Ordinary broadcast failure does not block other members. Start-publication failure is handled by the caller by revoking the entire round. */ }
            if (requireMatchPublication) CheckMatchPublication(room, cancellationToken);
        }
    }

    private sealed record RoomSettings(uint ModeId, ushort MapId, string MapString, uint SubModeId, uint RoomFlags, byte[] Wire);
}
