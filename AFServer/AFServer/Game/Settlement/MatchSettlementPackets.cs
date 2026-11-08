namespace AFServer.AssaultFire.Game;

/// <summary>
/// A11B shared rewards and observer lists. Both player lists retain the frozen native-statistics order
/// exactly.
/// </summary>
internal static class MatchSettlementPackets
{
    internal static byte[] EndMatch(MatchSettlementSnapshot snapshot, IReadOnlyDictionary<ulong, PlayerState> accounts)
    {
        if (snapshot.Start.Descriptor.ModeId is not (0x0204 or 0x1001 or 0x1002 or 0x1004 or 0x2001 or 0x2002 or 0x2003 or 0x2005) || snapshot.Players.Length > 32 ||
            snapshot.Spectators.Length > 32 || snapshot.Statistics.IsDefaultOrEmpty)
            throw new InvalidDataException("结算缺少已接入玩法的真实统计。");

        var writer = new TdrWriter().U32(GameWire.ZoneSuccess).U32(snapshot.Start.Descriptor.ModeId)
            .U16(snapshot.Players.Length);

        foreach (var participant in snapshot.Players)
        {
            var profile = participant.Member.Profile;
            var account = accounts[profile.Uin];
            uint reward = MatchSettlementReceipts.Reward(account, snapshot.Start.AllocationId);

            // The server fixes players at maximum level. Preserve actual current experience without inventing a TotalGameScore conversion or extra bonus.
            writer.U64(profile.Uin).String(profile.Nickname, 32).U8(participant.Camp)
                .U32(account.Progression.Experience).U32(0).U32(0).Zeros(6 * 4).U32(0).U32(0)
                .U32(checked((uint)account.Balance("gp"))).U32(reward).U32(reward).Zeros(3 * 4);
        }

        writer.U16(snapshot.Spectators.Length);

        foreach (var spectator in snapshot.Spectators)
        {
            var profile = spectator.Member.Profile;

            writer.U64(profile.Uin).String(profile.Nickname, 32).U8(spectator.Camp)
                .U32(accounts[profile.Uin].Progression.Experience);
        }

        writer.Bytes(snapshot.Statistics.AsSpan());

        return GameWire.Packet(0xA11B, writer);
    }
}
