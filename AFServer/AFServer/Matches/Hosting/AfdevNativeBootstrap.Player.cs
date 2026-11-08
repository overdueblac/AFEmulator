using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Read-only evidence for the actual native player associated with a relay source endpoint. Never creates,
/// logs in, or controls native Actors.
/// </summary>
public sealed record AfdevNativePlayerEvidence(Guid AllocationId, Guid AttemptId, uint ProcessId,
    string UpstreamEndpoint, ulong AuthenticatedUin, uint Connection, uint PlayerController,
    uint PlayerReplicationInfo, uint Pawn, bool LoginIdentityMatches, bool Possessed,
    float? X, float? Y, float? Z, string Stage, DateTimeOffset ObservedAtUtc);

public sealed partial class AfdevNativeBootstrap
{
    private static readonly (uint Address, string Original)[] PlayerObservationAnchors =
    [
        (0x012986EF, "8B47448B04988B8C249404000005E04F0000394804"),
        (0x0129880F, "8B47448B2C98433B5F48"),
        (0x0129033D, "897E60D9475CDD9E080200008DAEE04F0000"),
        (0x00DA514B, "8B8C24E80900008DBEE8000000578D86D40000005051E8DAADFDFF"),
        (0x00DA53C9, "E8B203CBFF8D8C2484070000894640"),
        (0x00762859, "8B454085C0740AC780700300000000000089AE70030000897540"),
        (0x0081159C, "8BBDDC0100008B018BB2DC010000"),
        (0x0081164B, "8BBDD80100008B118BB0D8010000"),
        (0x00818B64, "8B53543B565475108B4B583B4E5875088B535C3B565C"),
        (0x0046A21F, "8B763C85F675EE837F080074158B108BC88B8250010000FFD085C074078B704C"),
        (0x00494275, "8B068B501C8D4F48518BCEFFD2"),
        (0x00503360, "8B4E088B442424814E0C00400004894E088B4E28894660")
    ];

    /// <summary>
    /// Finds the unique connection in the current driver's array by the relay's exclusive source endpoint,
    /// then reads its URL, PC, PRI, and Pawn.
    /// </summary>
    /// <remarks>
    /// Read-only diagnostics are not new authorization. Returns null if the world, connection, or
    /// relationships change; callers reread later and must not extend a lease from a snapshot.
    /// </remarks>
    internal AfdevNativePlayerEvidence? ReadPlayerEvidence(IPEndPoint upstream, ulong uin, string nickname)
    {
        Require(Initialized && upstream.Address.Equals(IPAddress.Loopback), "玩家观察必须跟随本次 OPEN 并使用 relay 的 loopback 源端点。");
        VerifyNoNativeFailure();

        foreach (var anchor in PlayerObservationAnchors) Verify(anchor.Address, Convert.FromHexString(anchor.Original));

        uint world = ReadUInt32(GWorld);
        uint driver = ReadUInt32(world + 0xD8);
        byte[] array = Process.ReadMemory(driver + 0x44, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(array);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(8));

        Require(count <= capacity && capacity <= 64 && (count == 0 || IsPointer(data)), "原生 ClientConnections 数组无效或超出本轮观察预算。");

        uint connection = 0;

        for (uint index = 0; index < count; index++)
        {
            uint candidate = ReadUInt32(data + index * 4);

            if (!IsPointer(candidate) || !EndpointMatches(candidate, upstream)) continue;
            Require(connection == 0, "同一 relay 上游端点匹配多个原生连接，拒绝歧义。");
            connection = candidate;
        }

        if (connection == 0) return null;

        Require(ReadUInt32(connection + 0x60) == driver, "原生连接的 driver 与本次世界不一致。");

        string url = ReadNativeString(connection + 0xE8, 1024);
        bool identityMatches = url.Length != 0 && UrlIdentityMatches(url, uin, nickname);
        uint controller = ReadUInt32(connection + 0x40);
        uint pri = 0;
        uint pawn = 0;
        bool possessed = false;
        float? x = null, y = null, z = null;
        string stage = url.Length == 0 ? "NativeConnection" : "NativeLogin";

        if (url.Length != 0) Require(identityMatches, "原生 NetConnection 登录身份与当前 lease 或冻结昵称不一致。");
        if (controller != 0)
        {
            Require(IsPointer(controller) && ReadUInt32(controller + 0x370) == connection,
                "原生 PlayerController.Player 没有回指本次精确 NetConnection。");

            stage = "PlayerController";
            pawn = ReadUInt32(controller + 0x1D8);
            pri = ReadUInt32(controller + 0x1DC);

            if (pri != 0)
            {
                uint uinOffset = FindPropertyOffset(pri, "Uin", "StrProperty");
                uint nameOffset = FindPropertyOffset(pri, "PlayerName", "StrProperty");
                string priUin = ReadNativeString(pri + uinOffset, 32);
                string priName = ReadNativeString(pri + nameOffset, 64);

                Require(priUin == uin.ToString(CultureInfo.InvariantCulture) && priName == nickname,
                    "原生 PRI 的 Uin/PlayerName 与当前 lease 的冻结身份不一致。");
                stage = "PlayerReplicationInfo";
            }
            if (pawn != 0)
            {
                uint controllerOffset = FindPropertyOffset(pawn, "Controller", "ObjectProperty");
                uint locationOffset = FindPropertyOffset(pawn, "Location", "StructProperty");

                Require(locationOffset == 0x54, "原生 Pawn.Location 与已核实 Actor 位置布局不一致。");
                possessed = ReadUInt32(pawn + controllerOffset) == controller;

                byte[] position = Process.ReadMemory(pawn + locationOffset, 12);

                x = BinaryPrimitives.ReadSingleLittleEndian(position);
                y = BinaryPrimitives.ReadSingleLittleEndian(position.AsSpan(4));
                z = BinaryPrimitives.ReadSingleLittleEndian(position.AsSpan(8));
                Require(float.IsFinite(x.Value) && float.IsFinite(y.Value) && float.IsFinite(z.Value), "原生 Pawn 位置不是有限数值。");
                stage = possessed ? "PossessedPawn" : "Pawn";
            }
        }

        if (ReadUInt32(GWorld) != world || ReadUInt32(world + 0xD8) != driver ||
            !Process.ReadMemory(driver + 0x44, 12).AsSpan().SequenceEqual(array) ||
            !EndpointMatches(connection, upstream) || ReadUInt32(connection + 0x40) != controller ||
            ReadUInt32(connection + 0x60) != driver || ReadNativeString(connection + 0xE8, 1024) != url ||
            (controller != 0 && (ReadUInt32(controller + 0x370) != connection ||
                ReadUInt32(controller + 0x1D8) != pawn || ReadUInt32(controller + 0x1DC) != pri)))
            return null;

        return new AfdevNativePlayerEvidence(Workspace.AllocationId, AttemptId, Process.ProcessId,
            upstream.ToString(), uin, connection, controller, pri, pawn, identityMatches, possessed,
            x, y, z, stage, DateTimeOffset.UtcNow);
    }

    private bool EndpointMatches(uint connection, IPEndPoint expected)
    {
        byte[] address = Process.ReadMemory(connection + 0x4FE0, 8);

        return BinaryPrimitives.ReadUInt16LittleEndian(address) == 2 &&
            BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(2)) == expected.Port &&
            new IPAddress(address.AsSpan(4, 4)).Equals(expected.Address);
    }

    private uint FindPropertyOffset(uint instance, string name, string propertyClass)
    {
        var seenClasses = new HashSet<uint>();
        uint type = ReadUInt32(instance + 0x34);

        for (int depth = 0; type != 0 && depth < 32; depth++, type = ReadUInt32(type + 0x48))
        {
            Require(IsPointer(type) && seenClasses.Add(type), "原生属性类继承链无效。");

            var seenFields = new HashSet<uint>();
            uint field = ReadUInt32(type + 0x4C);

            for (int index = 0; field != 0 && index < 4096; index++, field = ReadUInt32(field + 0x3C))
            {
                Require(IsPointer(field) && seenFields.Add(field), "原生 UField.Children/Next 链无效。");
                if (!MatchesName(field, name)) continue;

                uint offset = ReadUInt32(field + 0x60);

                Require(ReadUInt32(field + 0x28) == type && MatchesName(ReadUInt32(field + 0x34), propertyClass) &&
                    ReadUInt32(field + 0x40) == 1 && offset is >= 0x3C and < 0x10000,
                    $"原生属性 {name} 类型、所有者或偏移不属于已核实布局。");

                return offset;
            }

            Require(field == 0, "原生属性链超出观察预算。");
        }

        throw new InvalidDataException($"当前原生类没有可核实的 {propertyClass} {name}。");
    }

    private string ReadNativeString(uint address, int maximumCharacters)
    {
        byte[] header = Process.ReadMemory(address, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        Require(count <= capacity && count <= maximumCharacters + 1 && capacity < 65536,
            "原生 FString 超出只读观察预算。");
        if (count == 0) return "";

        Require(IsPointer(data), "原生 FString 数据指针无效。");

        byte[] text = Process.ReadMemory(data, checked((int)count * 2));

        Require(text[^1] == 0 && text[^2] == 0 && Process.ReadMemory(address, 12).AsSpan().SequenceEqual(header),
            "原生 FString 缺少终止符或读取期间发生改变。");

        return new UnicodeEncoding(false, false, true).GetString(text.AsSpan(0, text.Length - 2));
    }

    private static bool UrlIdentityMatches(string url, ulong uin, string nickname)
    {
        string[] parts = url.Split('?').Skip(1).ToArray();
        string[] uins = parts.Where(value => value.StartsWith("Uin=", StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] names = parts.Where(value => value.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)).ToArray();

        return !url.Any(char.IsControl) && uins.Length == 1 && names.Length == 1 &&
            uins[0][4..] == uin.ToString(CultureInfo.InvariantCulture) && names[0][5..] == nickname;
    }

    private static bool IsPointer(uint value) => value is >= 0x10000 and < 0xFFF00000 && (value & 3) == 0;
}
