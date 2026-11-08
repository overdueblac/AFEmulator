namespace AFServer.AssaultFire.Game;

/// <summary>
/// Exact connection, seat, and observer state for a room member. Other connections with the same UIN do not
/// own this membership.
/// </summary>
public sealed class RoomMember(GameSession session, ushort seatIndex, bool observer)
{
    public GameSession Session { get; } = session;
    public ushort SeatIndex { get; set; } = seatIndex;
    public bool Observer { get; } = observer;
    public uint State { get; set; } = 8;
    public bool InMatch { get; set; }
}
