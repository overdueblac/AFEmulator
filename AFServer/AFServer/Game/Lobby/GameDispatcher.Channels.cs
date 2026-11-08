namespace AFServer.AssaultFire.Game;

// GEO and main-channel discovery remain serialized by the application entry point's shared business transaction lock.
public sealed partial class GameDispatcher
{
    // MainChnl_Normal avoids the client's beginner, advanced, and expert level bands, admitting players from levels 0–100.
    private const ushort NormalMainChannelType = 2;

    private async ValueTask HandleGeoAsync(GameSession session, ushort command, byte[] body, CancellationToken cancellationToken)
    {
        TdrWriter response;

        switch (command)
        {
            case 0x1001:
                response = new TdrWriter().U16(GameWire.GeoSuccess).U16(1).U32(0).U32(GameWire.Address(publicAddress, false))
                    .String(publicAddress, 128).U32(0).U32(1000);
                break;
            case 0x1000:
                response = new TdrWriter().U16(GameWire.GeoSuccess).U16(1).U32(GameWire.Address(publicAddress))
                    .String(publicAddress, 128).U16(zonePort).U32(online.Count).U32(1).U32(1).Zeros(24).U16(1);
                break;
            default:
                await repository.RecordUnhandledAsync(session.Uin, GameWire.GeoMagic, command, body, cancellationToken);
                return;
        }
        await session.SendAsync(GameWire.Packet((ushort)(command + 0x1000), response, GameWire.GeoMagic), cancellationToken);
    }
}
