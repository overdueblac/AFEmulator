using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher : IPveReviveCoinStore
{
    async ValueTask<int> IPveReviveCoinStore.GetCountAsync(MatchStartSnapshot snapshot, MatchStartMember member,
        CancellationToken cancellationToken)
    {
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!CanUseReviveCoins(snapshot, member)) return -1;

            var document = await repository.LoadPlayerAsync(member.Profile.Uin, cancellationToken).ConfigureAwait(false);

            return document is null ? 0 : PveReviveCoins.Count(new PlayerState(member.Profile.Uin, document));
        }
        finally { dispatchGate.Release(); }
    }

    async ValueTask<int> IPveReviveCoinStore.ConsumeAsync(MatchStartSnapshot snapshot, MatchStartMember member,
        uint sequence, CancellationToken cancellationToken)
    {
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!CanUseReviveCoins(snapshot, member)) return -1;

            // Reload the authoritative aggregate each time. Start-of-match counts must not overwrite match purchases, other inventory operations, or newer saves.
            var document = await repository.LoadPlayerAsync(member.Profile.Uin, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("复活币玩家存档不存在。");
            var player = new PlayerState(member.Profile.Uin, document);
            int remaining = PveReviveCoins.Apply(player, snapshot.AllocationId, member.Session.SessionId, sequence);

            if (remaining < 0) return -1;

            await repository.SavePlayerAsync(player.Uin, player.Document, cancellationToken).ConfigureAwait(false);
            protocolLog?.Invoke($"PVE_REVIVE_COMMITTED allocation={snapshot.AllocationId:N} session={member.Session.SessionId:N} " +
                $"sequence={sequence} remaining={remaining}");

            return remaining;
        }
        finally { dispatchGate.Release(); }
    }

    private bool CanUseReviveCoins(MatchStartSnapshot snapshot, MatchStartMember member)
        => PveReviveCoins.Supports(snapshot.Descriptor.ModeId) && !member.Observer && !member.Session.IsDisconnected &&
            member.Profile.Uin == member.Session.Uin && snapshot.Members.Contains(member) &&
            ReferenceEquals(activeSnapshots.GetValueOrDefault(snapshot.RoomId), snapshot) &&
            rooms.TryGetValue(snapshot.RoomId, out var room) && room.Started && !room.MatchReleasePending &&
            room.Members.Any(current => ReferenceEquals(current.Session, member.Session) && current.InMatch && !current.Observer);
}
