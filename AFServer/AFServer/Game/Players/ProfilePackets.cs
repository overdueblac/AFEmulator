namespace AFServer.AssaultFire.Game;

/// <summary>
/// Verified legacy-client profile layout. Field order and zero placeholders are protocol requirements and
/// must not be rearranged to match a business model.
/// </summary>
internal static partial class ProfilePackets
{
    internal static byte[] PlayerInfo(PlayerState player, ulong role)
    {
        var progression = player.Progression;
        var body = new TdrWriter().U16(GameWire.ZoneSuccess).U64(player.Uin).U32(0).String(player.Nickname, 32)
            .U32(player.Balance("ap")).U32(player.Balance("gp")).U32(player.Balance("mp")).U32(0)
            .U32(0).U16(0).U32(progression.Experience).Zeros(16).U64(role).U64(0).U64(0)
            .U32(progression.CardId).U32(progression.AchievementScore).Zeros(8).U16(progression.PrivacyFlags)
            .Zeros(16).U32(0).U32(0).U32(progression.Level).Zeros(12).U32(progression.AwardFlags).Zeros(8).U64(0).U64(0)
            .U32(0).U32(0).U16(0).U16(0).U32(0).U32(0).Zeros(8)
            .U16(0).U16(0).U16(0).U16(0).Zeros(8).U32(0).U32(0).U16(0).U16(0)
            .U64(0).U16(0).Zeros(8).U32(0);

        return GameWire.Packet(0xA005, body);
    }
}
