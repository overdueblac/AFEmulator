namespace AFServer.AssaultFire.Game;

// Synchronize the wallet downstream; purchase-success notifications require completed persistence.
public sealed partial class GameDispatcher
{
    private static async ValueTask SendWalletAsync(GameSession session, PlayerState player, CancellationToken cancellationToken)
    {
        // Send the persisted absolute balance. AP requires the TPBALANCE reason for the client to update GamePoint.
        // Use separate AP/GP/MP masks without purchase or reward reasons that might trigger duplicate business prompts.
        await session.SendAsync(ProfilePackets.Wallet(player, 0x01, 0x2A), cancellationToken);
        await session.SendAsync(ProfilePackets.Wallet(player, 0x02, 0), cancellationToken);
        await session.SendAsync(ProfilePackets.Wallet(player, 0x10, 0), cancellationToken);
    }
}
