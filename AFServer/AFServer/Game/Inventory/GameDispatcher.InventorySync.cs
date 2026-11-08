namespace AFServer.AssaultFire.Game;

// Inventory synchronization for login and purchases reuses the same verified packet layouts.
public sealed partial class GameDispatcher
{
    private static async ValueTask SendInventoryAsync(GameSession session, PlayerState player, CancellationToken cancellationToken)
    {
        foreach (var packet in ProfilePackets.Inventory(player)) await session.SendAsync(packet, cancellationToken);
    }
}
