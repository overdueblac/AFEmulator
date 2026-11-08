using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Exact-world, member, and memory boundaries shared by settlement reads. Called serially under the host
/// operation gate; does not own the native process.
/// </summary>
internal abstract class AfdevSettlementReader
{
    protected readonly AfdevOwnedProcess process;
    protected readonly MatchStartSnapshot start;
    protected readonly AfdevNativeEvidence binding;
    protected readonly QueryInspector reader;
    protected readonly Dictionary<ulong, MatchStartMember> members;
    protected readonly HashSet<Guid> retiredSessions = [];
    protected MatchSettlementSnapshot? completed;

    protected AfdevSettlementReader(AfdevOwnedProcess process, MatchStartSnapshot start, AfdevNativeEvidence world)
    {
        this.process = process;
        this.start = start;
        binding = world;
        reader = new QueryInspector(process);
        members = start.Members.ToDictionary(member => member.Profile.Uin);

        if (start.Descriptor.ModeId != world.ModeId || start.Descriptor.MapId != world.MapId ||
            start.AllocationId != world.AllocationId || start.Members.Length > 32 ||
            start.Members.Any(member => member.Profile.Uin == 0 || member.Profile.Uin != member.Session.Uin))
            throw new InvalidDataException("结算名单、玩法或分配身份不一致。");
    }

    internal abstract MatchSettlementSnapshot Capture(AfdevNativeEvidence world);

    internal void Retire(Guid sessionId)
    {
        if (!start.Members.Any(member => member.Session.SessionId == sessionId))
            throw new InvalidDataException("离场身份不属于本次冻结名单。");

        retiredSessions.Add(sessionId);
    }

    protected bool Bit(uint instance, string path)
    {
        uint address = Field(instance, path, "BoolProperty", 4);
        uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);
        uint mask = reader.U32(property + 0x84);

        if (mask == 0 || (mask & (mask - 1)) != 0)
            throw new InvalidDataException("结算状态属性不是单个原生布尔位。");

        return (reader.U32(address) & mask) != 0;
    }

    protected uint Field(uint instance, string path, string kind, uint size, uint? offset = null)
    {
        uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, kind, size);

        if (offset is { } expected && address != instance + expected)
            throw new InvalidDataException("结算字段布局变化：" + path);

        return address;
    }

    protected string ReadUin(uint pri, byte[] values)
    {
        Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12, 0xB64);

        return ReadString(values.AsSpan(0xB64, 12));
    }

    protected string ReadString(ReadOnlySpan<byte> header)
    {
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);

        if (count == 0) return "";
        if (data < 0x10000 || count > 64 || capacity < count)
            throw new InvalidDataException("原生结算字符串布局无效。");

        byte[] text = process.ReadMemory(data, checked((int)count * 2));

        if (text[^1] != 0 || text[^2] != 0)
            throw new InvalidDataException("原生结算字符串缺少终止符。");

        return Encoding.Unicode.GetString(text.AsSpan(0, text.Length - 2));
    }

    protected NativeArray ReadArray(uint address, int stride, int maximum, int reportMaximum = int.MaxValue)
    {
        byte[] header = process.ReadMemory(address, 12);
        uint data = U32(header, 0), count = U32(header, 4), capacity = U32(header, 8);

        if (count > maximum || capacity < count || (count != 0 && data < 0x10000))
            throw new InvalidDataException("原生结算数组超出边界。");

        int bytes = checked((int)Math.Min(count, (uint)reportMaximum) * stride);

        return new NativeArray(address, header, data, bytes == 0 ? [] : process.ReadMemory(data, bytes));
    }

    protected void CheckArray(NativeArray array)
    {
        if (!process.ReadMemory(array.Address, 12).AsSpan().SequenceEqual(array.Header))
            throw new InvalidDataException("原生结算数组在读取期间发生变化。");
    }

    protected void CheckWorld(AfdevNativeEvidence world)
    {
        if (world.AllocationId != start.AllocationId || world.AttemptId != binding.AttemptId ||
            world.InstanceId != binding.InstanceId || world.WorldGeneration != binding.WorldGeneration ||
            world.ModeId != binding.ModeId || world.MapId != binding.MapId || world.NetDriver != binding.NetDriver ||
            world.ProcessId != process.ProcessId || world.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            world.World != binding.World || world.GameInfo != binding.GameInfo || world.WorldInfo != binding.WorldInfo ||
            reader.U32(AfdevNativeBootstrap.GWorld) != world.World ||
            reader.U32(Field(world.WorldInfo, "Engine.WorldInfo.Game", "ObjectProperty", 4)) != world.GameInfo)
            throw new InvalidDataException("结算读取已经越过本次分配、启动尝试或原生世界。");

        reader.Identity(world.GameInfo);
    }

    protected void CheckCode(uint address, int length, string expected)
    {
        if (Convert.ToHexString(SHA256.HashData(process.ReadMemory(address, length))) != expected)
            throw new InvalidDataException($"结算原生报告代码指纹变化：0x{address:X8}。");
    }

    protected static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    protected sealed record NativeArray(uint Address, byte[] Header, uint Data, byte[] Bytes);
}
