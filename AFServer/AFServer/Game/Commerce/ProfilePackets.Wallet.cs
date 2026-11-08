namespace AFServer.AssaultFire.Game;

// The wallet wire layout shares the compatibility boundary of profile and inventory packets.
internal static partial class ProfilePackets
{
    // AF2014's A00A wallet notification is 36 bytes, distinct from the 19-byte item-operation notification.
    // UpdateFlag is a 32-bit mask and Reason a 16-bit enum, following ZN2C_NtfUpdatePlayerProperty.
    internal static byte[] Wallet(PlayerState player, uint flag, ushort reason) => GameWire.Packet(0xA00A,
        new TdrWriter().U32(flag).U16(reason).U32(player.Balance("ap")).U32(0).U32(player.Balance("gp"))
            .U32(player.Balance("mp")).U32(0).U32(0).U32(0).U16(0));
}
