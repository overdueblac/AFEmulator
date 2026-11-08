namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    private readonly Dictionary<ulong, MatchAdmissionRound> admissionRounds = [];

    /// <summary>
    /// Allows a trusted connection adapter to issue one-time eligibility for an already authenticated exact
    /// session. Obtain this instance from the current connection context, never by looking up another
    /// session through UIN, Options, or a global online dictionary. Send returned secrets only over that
    /// same authenticated connection. This method neither sends original client messages nor creates a
    /// simulator binding.
    /// </summary>
    public async ValueTask<MatchAdmissionTicket> IssueMatchAdmissionAsync(GameSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await dispatchGate.WaitAsync(cancellationToken);
        try
        {
            var room = RequireRoom(session);

            await RecoverFailedMatchAsync(room, cancellationToken);
            if (!room.Started || !room.Members.Single(member => member.Session == session).InMatch ||
                !admissionRounds.TryGetValue(room.RoomId, out var round))
                throw new InvalidOperationException("当前会话没有正在进行的回合。");
            return round.Issue(session);
        }
        finally { dispatchGate.Release(); }
    }

    /// <summary>
    /// Allows a trusted host adapter to retain the current allocation for its own lifetime, rather than
    /// looking it up per datagram by room ID. Old allocations become permanently invalid after round
    /// completion or failure; this query does not establish player identity.
    /// </summary>
    public async ValueTask<MatchAdmissionRound?> GetMatchAdmissionRoundAsync(ulong roomId,
        CancellationToken cancellationToken)
    {
        await dispatchGate.WaitAsync(cancellationToken);
        try
        {
            if (!rooms.TryGetValue(roomId, out var room)) return null;
            await RecoverFailedMatchAsync(room, cancellationToken);
            return admissionRounds.GetValueOrDefault(roomId);
        }
        finally { dispatchGate.Release(); }
    }

    private void RevokeMatchAdmission(ulong roomId)
    {
        if (admissionRounds.Remove(roomId, out var round)) round.RevokeAll();
    }
}
