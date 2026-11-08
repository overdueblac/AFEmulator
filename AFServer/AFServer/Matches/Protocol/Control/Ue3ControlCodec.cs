using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Protocol;

public abstract record Ue3ControlMessage(byte MessageType);
public sealed record Ue3Hello(byte EndianFlag, uint VersionField1, uint VersionField2) : Ue3ControlMessage(0);
public sealed record Ue3Welcome(string LevelName, string GameName) : Ue3ControlMessage(1);
public sealed record Ue3Challenge(uint NetworkVersion, string Challenge) : Ue3ControlMessage(3);
public sealed record Ue3Netspeed(int Rate) : Ue3ControlMessage(4);
public sealed record Ue3Login(string ClientResponse, string RequestUrl) : Ue3ControlMessage(5);
public sealed record Ue3Failure(string Reason) : Ue3ControlMessage(6);
public readonly record struct Ue3PackageGuid(uint A, uint B, uint C, uint D);
/// <summary>
/// NMT 7 structure verified through dispatch/serialization calls. Unnamed fields retain original order
/// without inferred business meanings.
/// </summary>
public sealed record Ue3PackageAnnouncement(Ue3PackageGuid Guid, string PackageName, string StringField2,
    uint WordField1, int RemoteGeneration, string StringField3) : Ue3ControlMessage(7);
public sealed record Ue3PackageHave(Ue3PackageGuid Guid, int RemoteGeneration) : Ue3ControlMessage(8);
/// <summary>
/// NMT 12/13/14 contain only a four-word GUID; message names/numbers are documented in
/// docs/research/package-negotiation-evidence.md.
/// </summary>
public sealed record Ue3PackageSkip(Ue3PackageGuid Guid) : Ue3ControlMessage(12);
public sealed record Ue3PackageAbort(Ue3PackageGuid Guid) : Ue3ControlMessage(13);
public sealed record Ue3PackageUnload(Ue3PackageGuid Guid) : Ue3ControlMessage(14);
/// <summary>
/// This branch's NMT_Join contains eight bytes; identity semantics and account binding remain unverified.
/// </summary>
public sealed record Ue3Join(ulong OpaqueId) : Ue3ControlMessage(9);

public enum Ue3ControlDecodeStatus
{
    Complete,
    UnknownMessage,
    UnsupportedByteOrder
}

/// <summary>
/// Complete means only that recognized fields consume the payload, not authentication, successful joining,
/// or a runnable match. Unknown control messages explicitly remain incomplete; never skip them and guess
/// later boundaries. Package decoding implements no matching, index negotiation, or Actor-replication state
/// machine.
/// </summary>
public sealed record Ue3ControlStream(
    IReadOnlyList<Ue3ControlMessage> Messages,
    Ue3ControlDecodeStatus Status,
    int ConsumedBytes,
    byte[] UnparsedPayload);

/// <summary>
/// Minimal control-message grammar from assaultfire_server_v143b.py:9556–9740 and bridge:215–287. Later
/// static evidence: TGame_AFDEV.exe tables 0x011EE778 and server 0x00DA5C20. NMT 7/8/9 and Login details
/// are referenced in docs/research/native-protocol-evidence.md. Do not apply another UE branch's empty Join
/// layout.
/// </summary>
public static class Ue3ControlCodec
{
    public static byte[] EncodeChallenge(uint networkVersion, string challenge)
    {
        byte[] prefix = new byte[5];

        prefix[0] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1), networkVersion);
        return Combine(prefix, FStringCodec.Write(challenge));
    }

    public static byte[] EncodeWelcome(string levelName, string gameName)
        // This branch has only two FStrings; a third RedirectURL would be read as the next control message.
        => Combine([1], FStringCodec.Write(levelName), FStringCodec.Write(gameName));

    public static byte[] EncodeLogin(string clientResponse, string requestUrl)
        => Combine([5], FStringCodec.Write(clientResponse), FStringCodec.Write(requestUrl));

    public static byte[] EncodePackageAnnouncement(Ue3PackageAnnouncement message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] words = new byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(words, message.WordField1);
        BinaryPrimitives.WriteInt32LittleEndian(words.AsSpan(4), message.RemoteGeneration);
        return Combine(GuidPrefix(7, message.Guid), FStringCodec.Write(message.PackageName),
            FStringCodec.Write(message.StringField2), words, FStringCodec.Write(message.StringField3));
    }

    public static byte[] EncodePackageHave(Ue3PackageHave message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] generation = new byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(generation, message.RemoteGeneration);
        return Combine(GuidPrefix(8, message.Guid), generation);
    }

    public static byte[] EncodePackageSkip(Ue3PackageSkip message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GuidPrefix(12, message.Guid);
    }

    public static byte[] EncodePackageAbort(Ue3PackageAbort message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GuidPrefix(13, message.Guid);
    }

    public static byte[] EncodePackageUnload(Ue3PackageUnload message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GuidPrefix(14, message.Guid);
    }

    public static byte[] EncodeJoin(ulong opaqueId)
    {
        byte[] result = new byte[9];

        result[0] = 9;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(1), opaqueId);
        return result;
    }

    public static Ue3ControlStream DecodeKnownStream(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > NativeDsDatagramCodec.MaximumPlaintextBytes)
            throw new InvalidDataException("控制消息载荷超出已验证上限。");

        var messages = new List<Ue3ControlMessage>();
        int position = 0;

        while (position < payload.Length)
        {
            int start = position;
            byte type = payload[position++];

            switch (type)
            {
                case 0:
                    // 0x011D8600 confirms the 1+4+4 layout too; it does not establish version-negotiation rules.
                    Require(payload, position, 9);

                    byte endian = payload[position++];
                    uint firstVersion = ReadUInt32(payload, ref position);
                    uint secondVersion = ReadUInt32(payload, ref position);

                    messages.Add(new Ue3Hello(endian, firstVersion, secondVersion));
                    if (endian != 1)
                        return Incomplete(messages, Ue3ControlDecodeStatus.UnsupportedByteOrder, start, payload);
                    break;
                case 1:
                    string level = FStringCodec.Read(payload, ref position);
                    string game = FStringCodec.Read(payload, ref position);

                    messages.Add(new Ue3Welcome(level, game));
                    break;
                case 3:
                    uint version = ReadUInt32(payload, ref position);
                    string challenge = FStringCodec.Read(payload, ref position);

                    messages.Add(new Ue3Challenge(version, challenge));
                    break;
                case 4:
                    messages.Add(new Ue3Netspeed(unchecked((int)ReadUInt32(payload, ref position))));
                    break;
                case 5:
                    string response = FStringCodec.Read(payload, ref position);
                    string url = FStringCodec.Read(payload, ref position);

                    messages.Add(new Ue3Login(response, url));
                    // Table[5] -> 0x011E31D0 and server 0x00D7FF40 each read only two FStrings.
                    // Parse Python's retained tail as subsequent NMTs rather than swallowing unknown commands.
                    break;
                case 6:
                    messages.Add(new Ue3Failure(FStringCodec.Read(payload, ref position)));
                    break;
                case 7:
                    Ue3PackageGuid announcedGuid = ReadGuid(payload, ref position);
                    string name = FStringCodec.Read(payload, ref position);
                    string stringField2 = FStringCodec.Read(payload, ref position);
                    uint wordField1 = ReadUInt32(payload, ref position);
                    int announcedGeneration = unchecked((int)ReadUInt32(payload, ref position));
                    string stringField3 = FStringCodec.Read(payload, ref position);

                    messages.Add(new Ue3PackageAnnouncement(announcedGuid, name, stringField2, wordField1, announcedGeneration, stringField3));
                    break;
                case 8:
                    Ue3PackageGuid haveGuid = ReadGuid(payload, ref position);
                    int haveGeneration = unchecked((int)ReadUInt32(payload, ref position));

                    messages.Add(new Ue3PackageHave(haveGuid, haveGeneration));
                    break;
                case 9:
                    Require(payload, position, 8);
                    messages.Add(new Ue3Join(BinaryPrimitives.ReadUInt64LittleEndian(payload[position..])));
                    position += 8;
                    break;
                case 12:
                    messages.Add(new Ue3PackageSkip(ReadGuid(payload, ref position)));
                    break;
                case 13:
                    messages.Add(new Ue3PackageAbort(ReadGuid(payload, ref position)));
                    break;
                case 14:
                    messages.Add(new Ue3PackageUnload(ReadGuid(payload, ref position)));
                    break;
                default:
                    return Incomplete(messages, Ue3ControlDecodeStatus.UnknownMessage, start, payload);
            }
        }
        return new Ue3ControlStream(messages.AsReadOnly(), Ue3ControlDecodeStatus.Complete, position, []);
    }

    private static Ue3ControlStream Incomplete(List<Ue3ControlMessage> messages, Ue3ControlDecodeStatus status, int position, ReadOnlySpan<byte> payload)
        => new(messages.AsReadOnly(), status, position, payload[position..].ToArray());

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, ref int position)
    {
        Require(payload, position, 4);

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);

        position += 4;
        return value;
    }

    private static Ue3PackageGuid ReadGuid(ReadOnlySpan<byte> payload, ref int position)
        => new(ReadUInt32(payload, ref position), ReadUInt32(payload, ref position),
            ReadUInt32(payload, ref position), ReadUInt32(payload, ref position));

    private static byte[] GuidPrefix(byte type, Ue3PackageGuid guid)
    {
        byte[] prefix = new byte[17];

        prefix[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1), guid.A);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(5), guid.B);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(9), guid.C);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(13), guid.D);
        return prefix;
    }

    private static void Require(ReadOnlySpan<byte> payload, int position, int count)
    {
        if (position < 0 || count > payload.Length - position)
            throw new InvalidDataException("控制消息字段已截断。");
    }

    private static byte[] Combine(byte[] prefix, params byte[][] values)
    {
        int length = prefix.Length;

        foreach (byte[] value in values) length += value.Length;
        if (length > NativeDsDatagramCodec.MaximumPlaintextBytes)
            throw new ArgumentException("控制消息超过已验证的载荷上限。", nameof(values));

        byte[] result = new byte[length];

        prefix.CopyTo(result, 0);

        int position = prefix.Length;

        foreach (byte[] value in values)
        {
            value.CopyTo(result, position);
            position += value.Length;
        }
        return result;
    }
}
