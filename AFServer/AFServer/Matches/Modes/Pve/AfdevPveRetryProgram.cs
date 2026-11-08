using System.Buffers.Binary;
using System.Text;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Script assembler for an exclusive PVE process. Object references retain the engine's eight-byte layout.
/// Install/update only while the game thread is not executing script.
/// </summary>
internal sealed class AfdevPveRetryProgram(AfdevOwnedProcess process)
{
    private readonly QueryInspector reader = new(process);
    private readonly Dictionary<string, uint> known = [];
    private readonly Dictionary<uint, List<uint>> byName = [];
    private readonly Dictionary<string, uint> nameIndices = [];
    private readonly Dictionary<uint, string> paths = [];
    private readonly Dictionary<uint, QueryObject> identities = [];
    private uint[]? objectSlots;
    private HashSet<uint>? liveObjects;
    private IReadOnlyList<uint>? objects;
    private bool namesIndexed;

    // Reuse instance headers only within the same synchronous mailbox wait; discard immediately across native-execution boundaries to avoid GC slot reuse.
    internal void BeginRead()
    {
        paths.Clear();
        identities.Clear();
        byName.Clear();
        objectSlots = null;
        liveObjects = null;
        objects = null;
        namesIndexed = false;
    }

    internal uint Class(uint item) => Identity(item).Class;

    internal IReadOnlyList<uint> Objects()
    {
        if (objects is not null) return objects;

        uint count = reader.U32(0x02053EAC), table = reader.U32(0x02053EA8);

        if (count > 1_000_000 || table == 0) throw new InvalidDataException("PVE 对象表超界。");

        byte[] data = process.ReadMemory(table, checked((int)count * 4));
        var result = new List<uint>();

        objectSlots = new uint[count];
        for (int index = 0; index < count; index++)
        {
            uint item = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));

            objectSlots[index] = item;
            if (item == 0) continue;

            result.Add(item);
        }

        objects = result;
        liveObjects = result.ToHashSet();

        return objects;
    }

    private QueryObject Identity(uint item)
    {
        if (identities.TryGetValue(item, out var cached)) return cached;
        if (item < 0x10000) throw new InvalidDataException("PVE 对象地址无效。");

        Objects();

        byte[] header = process.ReadMemory(item, 0x38);
        uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
        uint index = Read(4);

        if (index >= objectSlots!.Length || objectSlots[index] != item)
            throw new InvalidDataException("PVE 对象已不属于当前对象表。");

        var identity = new QueryObject(item, index, Read(0x28), Read(0x2C), Read(0x30), Read(0x34));

        identities.Add(item, identity);

        return identity;
    }

    private void IndexNames()
    {
        if (namesIndexed) return;

        foreach (uint item in Objects())
        {
            uint index = Identity(item).NameIndex;

            if (!byName.TryGetValue(index, out var candidates)) byName.Add(index, candidates = []);

            candidates.Add(item);
        }

        foreach (uint index in byName.Keys) nameIndices[reader.Name(index)] = index;

        namesIndexed = true;
    }

    internal uint Find(string path)
    {
        if (known.TryGetValue(path, out uint cached))
        {
            // After GC, an address may leave the object table or be reused by a same-named object with another Outer.
            // Check the current table before the complete path. A readable old address alone does not justify reusing a binding.
            Objects();
            if (liveObjects!.Contains(cached) && Path(cached) == path) return cached;

            known.Remove(path);
        }

        // Numeric instance-name suffixes are not FName text. Resolve usual paths through static fields or registered level nodes.
        string leaf = path.Split('.')[^1];
        int underscore = leaf.LastIndexOf('_');
        string lookup = underscore >= 0 && uint.TryParse(leaf.AsSpan(underscore + 1), out _) ? leaf[..underscore] : leaf;

        IndexNames();

        foreach (string name in new[] { leaf, lookup }.Distinct(StringComparer.Ordinal))
        {
            if (!nameIndices.TryGetValue(name, out uint index) || !byName.TryGetValue(index, out var candidates)) continue;

            foreach (uint candidate in candidates)
            {
                if (Path(candidate) != path) continue;

                known[path] = candidate;

                return candidate;
            }
        }

        throw new InvalidDataException("PVE 回合重试缺少原生对象：" + path);
    }

    internal string Path(uint item)
    {
        if (paths.TryGetValue(item, out string? cached)) return cached;

        uint start = item;
        var names = new List<string>();
        var seen = new HashSet<uint>();

        while (item != 0)
        {
            if (seen.Count >= 64 || !seen.Add(item)) throw new InvalidDataException("PVE 对象外层链无效。");

            var identity = Identity(item);
            string name = reader.Name(identity.NameIndex);

            names.Add(identity.NameNumber == 0 ? name : name + "_" + (identity.NameNumber - 1));
            item = identity.Outer;
        }

        names.Reverse();

        string result = string.Join('.', names);

        paths[start] = result;

        return result;
    }

    internal byte[] Pointer(uint value) => [.. BitConverter.GetBytes(value), 0, 0, 0, 0];
    internal byte[] Object(uint value) => [0x20, .. Pointer(value)];
    internal byte[] Variable(string path, bool local = false) => [local ? (byte)0 : (byte)1, .. Pointer(Find(path))];
    internal byte[] Integer(int value) => [0x1D, .. BitConverter.GetBytes(value)];
    internal byte[] Float(uint bits) => [0x1E, .. BitConverter.GetBytes(bits)];
    internal byte[] Text(string value) => [0x34, .. Encoding.Unicode.GetBytes(value + '\0')];
    internal byte[] Context(byte[] instance, byte[] expression, uint result = 0) =>
        [0x19, .. instance, .. BitConverter.GetBytes(checked((ushort)expression.Length)), .. Pointer(result), 0, .. expression];
    internal byte[] Call(uint target, string path, params byte[][] args) => Context(Object(target),
        [0x1C, .. Pointer(Find(path)), .. args.SelectMany(item => item), 0x16]);
    internal byte[] Final(string path, params byte[][] args) => [0x1C, .. Pointer(Find(path)), .. args.SelectMany(item => item), 0x16];
    internal byte[] Property(uint target, string path, bool boolean = false)
    {
        byte[] value = Variable(path);

        return Context(Object(target), boolean ? [0x2D, .. value] : value, Find(path));
    }
    internal byte[] Set(uint target, string path, byte[] value, bool boolean = false) =>
        [boolean ? (byte)0x14 : (byte)0x0F, .. Property(target, path, boolean), .. value];
    internal byte[] ClearArray(uint target, string path) => [0x0F, 0x36, .. Property(target, path), 0x25];

    internal byte[] Gateway(uint online, int command, byte[] text, byte[] argument) => Context(Object(online),
        Final("UTGame.TGOnlineDS.GetFunctionPropCount", text, Integer(command), argument),
        Find("UTGame.TGOnlineDS.GetFunctionPropCount.ReturnValue"));
    internal static byte[] Positive(byte[] value) => [0x97, .. value, 0x25, 0x16];

    internal byte[] Verify(AfdevPveRetryScripts.Layout layout)
    {
        uint function = Find(layout.Path);
        int length = reader.U32(function + 0x58) is uint size && size <= 65534 ? (int)size : throw new InvalidDataException("PVE 脚本超界。");

        if (reader.Path(reader.U32(function + 0x34)) != "Core.Function" ||
            reader.U16(function + 0xB0) != 0 || reader.U32(function + 0xC8) != 0x0048DB10 || reader.U32(function + 0x5C) < length)
            throw new InvalidDataException("PVE 重试函数布局不同：" + layout.Path);

        byte[] original = process.ReadMemory(reader.U32(function + 0x54), length);
        byte[] normalized = original.ToArray();

        foreach (var (offset, path) in layout.References)
        {
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(offset));

            if (BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(offset + 4)) != 0 ||
                (path.Length == 0 ? pointer != 0 : reader.Path(pointer) != path))
                throw new InvalidDataException("PVE 重试脚本引用不同：" + layout.Path);

            normalized.AsSpan(offset, 8).Clear();
            if (path.Length > 0) known[path] = pointer;
        }
        foreach (var (offset, name, number) in layout.Names)
        {
            if (reader.Name(BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(offset))) != name ||
                BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(offset + 4)) != number)
                throw new InvalidDataException("PVE 重试脚本名称不同：" + layout.Path);

            normalized.AsSpan(offset, 8).Clear();
        }

        if (!normalized.AsSpan().SequenceEqual(Convert.FromHexString(layout.Hex)))
            throw new InvalidDataException("PVE 重试脚本指令不同：" + layout.Path);

        return original;
    }

    // UFunctions/new script pages survive for this process's whole round. Never replace scripts on active stacks or unload shared classes.
    internal void Install(string path, byte[] code)
    {
        if (code.Length is < 3 or >= 65535) throw new InvalidDataException("PVE 重试脚本长度无效。");

        uint function = Find(path), page = process.AllocateMemory(code.Length);

        if (path is "UTGame.TGGame.StoreCheckPoint" or "UTGame.TGGame.RestoreCheckPoint")
        {
            uint flags = reader.U32(function + 0xAC);

            if ((flags != 0x20000 && flags != 0x20002) || reader.U16(function + 0xBE) != 0 || reader.U32(function + 0x50) != 0)
                throw new InvalidDataException("PVE 空检查点槽位的函数元数据不同。");

            // The original package declares the function without FUNC_Defined. Adding a body requires setting that flag too.
            process.WriteMemory(function + 0xAC, BitConverter.GetBytes(flags | 2));
        }

        process.WriteMemory(page, code);
        process.WriteMemory(function + 0x54, [.. BitConverter.GetBytes(page), .. BitConverter.GetBytes(code.Length), .. BitConverter.GetBytes(code.Length)]);

        if (!process.ReadMemory(page, code.Length).AsSpan().SequenceEqual(code))
            throw new InvalidDataException("PVE 重试脚本回读不一致。");
    }

    internal sealed class Code
    {
        private readonly List<byte> bytes = [];
        private readonly List<(int Offset, string Label)> jumps = [];
        private readonly Dictionary<string, int> labels = [];
        internal int Length => bytes.Count;
        internal void Emit(params byte[][] code) => bytes.AddRange(code.SelectMany(item => item));
        internal void Label(string name) => labels.Add(name, bytes.Count);
        internal void Unless(string label, byte[] expression)
        {
            bytes.Add(0x07); jumps.Add((bytes.Count, label)); bytes.AddRange(new byte[2]); bytes.AddRange(expression);
        }
        internal byte[] Finish(bool appendReturn = true)
        {
            if (appendReturn) bytes.AddRange(new byte[] { 4, 0x0B, 0x53 });

            byte[] code = bytes.ToArray();

            foreach (var (offset, label) in jumps)
                BinaryPrimitives.WriteUInt16LittleEndian(code.AsSpan(offset), checked((ushort)labels[label]));

            return code;
        }
    }
}
