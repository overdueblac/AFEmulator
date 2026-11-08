namespace AFServer.AssaultFire.Protocol;

public sealed record TGameAuth(uint Uin, uint AuthType, uint AdvertisedMode, uint ServiceId, byte[] Ticket);
public sealed record TGameCryptoState(byte[] Key, int Mode);

/// <summary>
/// TPDU frame codec. Downstream application packets do not include the client's four-byte sequence prefix.
/// </summary>
public static class TGameProtocol
{
    public static readonly byte[] Challenge = "LOCAL_SYN_RAND01"u8.ToArray();
    public static readonly byte[] SessionKey = "LOCAL_GAME_KEY01"u8.ToArray();

    public static TGameAuth ParseAuth(byte[] frame)
    {
        if (frame.Length < 32 || frame[0] != 0x55 || frame[1] != 0x0e || frame[2] != 3) throw new InvalidDataException("TGame AUTH 帧无效。");

        uint length = Wire.Read32(frame, 28);

        if (length > (uint)(frame.Length - 32)) throw new InvalidDataException("TGame 票据不完整。");
        return new(Wire.Read32(frame, 24), Wire.Read32(frame, 20), Wire.Read32(frame, 12), Wire.Read32(frame, 16), frame[32..(32 + (int)length)]);
    }

    public static byte[] Syn(TGameCryptoState state)
    {
        byte[] encrypted = LegacyCrypto.Encrypt(Challenge, state.Key, state.Mode);

        return new Wire().Bytes([0x55, 0x0e, 8, 0]).U32((uint)(13 + encrypted.Length)).U32(0).U8((byte)encrypted.Length).Bytes(encrypted).ToArray();
    }

    public static byte[] ChangeKey(TGameCryptoState state)
    {
        byte[] encrypted = LegacyCrypto.Encrypt(SessionKey, state.Key, state.Mode);

        return new Wire().Bytes([0x55, 0x0e, 1, 0]).U32((uint)(16 + encrypted.Length)).U32(0).U16(0).U16((ushort)encrypted.Length).Bytes(encrypted).ToArray();
    }

    public static byte[] Application(byte[] plain, TGameCryptoState state)
    {
        byte[] encrypted = LegacyCrypto.Encrypt(plain, state.Key, state.Mode);

        return new Wire().Bytes([0x55, 0x0e, 0, 4]).U32(12).U32((uint)encrypted.Length).Bytes(encrypted).ToArray();
    }

    public static byte[] TaccResponse(uint uin, uint timestamp)
    {
        byte[] body = new Wire().U16(0).U32(uin).U8(0).U32(timestamp).U16(0).U16(1).U32(timestamp).U32(0x01010101).U16(6).U32(0).U16(1).ToArray();
        byte[] app = new Wire().U32((uint)(14 + body.Length)).U16(0xc8).U16(2).U16(2).U32(0).Bytes(body).ToArray();

        // tacc_2_1.dll cmd05 reads plaintext TACCCliPkg directly, without decryption or TPDU sequence parsing.
        return new Wire().Bytes([0, 0x0c, 5, 9, 4]).U32((uint)app.Length).Bytes(app).ToArray();
    }
}
