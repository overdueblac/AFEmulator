namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    // Access only under the existing lobby transaction lock. Retain frozen results on send failure so retries neither resample another moment nor credit twice.
    private readonly Dictionary<GameSession, PendingSettlement> settlements = [];

    private async ValueTask QuitMatchAsync(GameSession session, PlayerState player, CancellationToken cancellationToken)
    {
        var room = RequireRoom(session);
        var active = activeSnapshots.GetValueOrDefault(room.RoomId);

        settlements.TryGetValue(session, out var pending);

        if (pending is not null && (pending.Snapshot.Start.RoomId != room.RoomId ||
            (active is not null && pending.Snapshot.Start.AllocationId != active.AllocationId)))
        {
            settlements.Remove(session);
            pending = null;
        }

        try
        {
            if (pending is null)
            {
                if (active is null || !room.Started || !room.Members.Any(member => member.Session == session && member.InMatch) ||
                    !active.Members.Any(member => ReferenceEquals(member.Session, session)) ||
                    allocator is not IMatchSettlementAllocator source)
                    throw new InvalidOperationException("本会话没有可结算的活动原生对局。");

                var snapshot = await source.ReadSettlementAsync(active, cancellationToken);

                if (!ReferenceEquals(snapshot.Start, active) ||
                    (!snapshot.Players.Any(row => ReferenceEquals(row.Member.Session, session)) &&
                     !snapshot.Spectators.Any(row => ReferenceEquals(row.Member.Session, session))))
                    throw new InvalidDataException("未取得本会话的真实原生参战统计，不能发送空结算。");

                pending = new PendingSettlement(snapshot);
                settlements.Add(session, pending);
            }

            if (pending.Packet is null)
                pending.Packet = await CommitSettlementAsync(pending.Snapshot, session, player, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Database failure must not look like successful settlement. Retain the original match and frozen results so later A117 requests can retry using save receipts.
            protocolLog?.Invoke($"SETTLEMENT_FAILED session={session.SessionId:N} allocation={active?.AllocationId:N} " +
                $"kind={error.GetType().Name} message={error.Message}");
            await SendAsync(session, 0xA118, new TdrWriter().U16(0x0100), cancellationToken);

            return;
        }

        await SendAsync(session, 0xA118, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
        await session.SendAsync(pending.Packet, cancellationToken);
        await SendWalletAsync(session, player, cancellationToken);

        if (PveReviveCoins.Supports(pending.Snapshot.Start.Descriptor.ModeId))
            await SendInventoryAsync(session, player, cancellationToken);

        // Observers may remain on the results page after the last participant releases the host. Remaining members of the same match can reuse the complete results.
        if (pending.Snapshot.Completed || room.Members.All(member => member.Session == session || member.Observer || !member.InMatch))
        {
            foreach (var member in pending.Snapshot.Start.Members.Where(member => pending.Snapshot.Completed || member.Observer))
            {
                if (member.Session.IsDisconnected || !room.Members.Any(current => ReferenceEquals(current.Session, member.Session)) ||
                    settlements.ContainsKey(member.Session))
                    continue;

                if (pending.Snapshot.Players.Any(row => ReferenceEquals(row.Member, member)) ||
                    pending.Snapshot.Spectators.Any(row => ReferenceEquals(row.Member, member)))
                    settlements.Add(member.Session, new PendingSettlement(pending.Snapshot) { Packet = pending.Packet });
            }
        }

        if (!pending.Returned)
        {
            ushort seat = room.Members.Single(member => ReferenceEquals(member.Session, session)).SeatIndex;

            await ReturnFromMatchAsync(room, session, cancellationToken);
            pending.Returned = true;
            await BroadcastAsync(room, 0xA119, new TdrWriter().U16(seat), cancellationToken, session);
        }

        protocolLog?.Invoke($"SETTLEMENT_SENT session={session.SessionId:N} allocation={pending.Snapshot.Start.AllocationId:N} " +
            $"completed={pending.Snapshot.Completed} players={pending.Snapshot.Players.Length} " +
            $"sample={pending.Snapshot.ObservedAtUtc:O}");
    }

    private async Task<byte[]> CommitSettlementAsync(MatchSettlementSnapshot snapshot, GameSession requester,
        PlayerState currentPlayer, CancellationToken cancellationToken)
    {
        var accounts = new Dictionary<ulong, PlayerState> { [currentPlayer.Uin] = currentPlayer };

        foreach (var member in snapshot.Players.Select(row => row.Member).Concat(snapshot.Spectators.Select(row => row.Member)))
        {
            if (accounts.ContainsKey(member.Profile.Uin)) continue;

            var document = await repository.LoadPlayerAsync(member.Profile.Uin, cancellationToken) ??
                throw new InvalidDataException("结算成员的权威玩家存档不存在。");

            accounts.Add(member.Profile.Uin, new PlayerState(member.Profile.Uin, document));
        }

        foreach (var participant in snapshot.Players)
        {
            // Early departure commits only that player. At match end, commit precomputed results per player; one failure does not roll back other committed receipts.
            if (!snapshot.Completed && !ReferenceEquals(participant.Member.Session, requester)) continue;

            var account = accounts[participant.Member.Profile.Uin];

            if (MatchSettlementReceipts.Apply(account, snapshot, participant))
                await repository.SavePlayerAsync(account.Uin, account.Document, cancellationToken);
        }

        return MatchSettlementPackets.EndMatch(snapshot, accounts);
    }

    private sealed class PendingSettlement(MatchSettlementSnapshot snapshot)
    {
        internal MatchSettlementSnapshot Snapshot { get; } = snapshot;
        internal byte[]? Packet;
        internal bool Returned;
    }
}
