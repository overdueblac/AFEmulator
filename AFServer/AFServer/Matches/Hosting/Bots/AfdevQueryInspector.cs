using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Reads objects and reflection from the current native process. The default cache reuses only fixed
/// process-local name/field lookups; live identity and values are read afresh. Callers still validate
/// function layouts and command proofs. The cache dies with the owned process object.
/// </summary>
sealed class QueryInspector(AfdevOwnedProcess process, bool cacheMetadata = true)
{
    private static readonly ConditionalWeakTable<AfdevOwnedProcess, ReflectionCache> ProcessCaches = new();
    private readonly ReflectionCache? cache = cacheMetadata ? ProcessCaches.GetValue(process, _ => new ReflectionCache()) : null;

    public uint U32(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(process.ReadMemory(address, 4));
    public ushort U16(uint address) => BinaryPrimitives.ReadUInt16LittleEndian(process.ReadMemory(address, 2));
    public QueryObject Identity(uint address)
    {
        if (address < 0x10000)
            throw new InvalidDataException("Null/low UObject.");

        uint index = U32(address + 4), count = U32(0x02053EAC), table = U32(0x02053EA8);

        if (index > int.MaxValue || index >= count || table == 0 || U32(table + index * 4) != address)
            throw new InvalidDataException("UObject array index/back-pointer differs.");
        return new QueryObject(address, index, U32(address + 0x28), U32(address + 0x2C), U32(address + 0x30), U32(address + 0x34));
    }

    public IReadOnlyList<QueryObject> IdentityClosure(params uint[] addresses)
    {
        var result = new List<QueryObject>();
        var pending = new Queue<uint>(addresses);
        var seen = new HashSet<uint>();

        while (pending.TryDequeue(out uint pointer))
        {
            if (pointer == 0 || !seen.Add(pointer))
                continue;
            if (seen.Count > 32)
                throw new InvalidDataException("Identity closure exceeds fixed command bounds.");

            QueryObject item = Identity(pointer);

            result.Add(item);
            pending.Enqueue(item.Outer);
            pending.Enqueue(item.Class);
        }

        return result;
    }

    public string Name(uint index)
    {
        uint count = U32(0x02041B6C);

        if (index >= count)
            throw new InvalidDataException("FName index invalid.");

        uint entry = U32(U32(0x02041B68) + 4 * index);
        uint tag = U32(entry + 8);

        if (tag >> 1 != index)
            throw new InvalidDataException("FName identity differs.");

        // FName text is fixed and interned in this process. Reread entries and tags each time to avoid another name table or slot.
        if (cache != null && cache.Names.TryGetValue(index, out NameEntry? known) &&
            known.Entry == entry && known.Tag == tag)
            return known.Text;

        uint width = (tag & 1) == 0 ? 1u : 2u;
        var text = new StringBuilder();

        for (uint index2 = 0; index2 < 1024; index2++)
        {
            byte[] bytes = process.ReadMemory(entry + 16 + index2 * width, (int)width);
            char value = (char)(width == 1 ? bytes[0] : BinaryPrimitives.ReadUInt16LittleEndian(bytes));

            if (value == 0)
            {
                string result = text.ToString();

                if (cache != null && cache.Names.Count < ReflectionCache.MaximumNames)
                    cache.Names[index] = new NameEntry(entry, tag, result);

                return result;
            }
            text.Append(value);
        }

        throw new InvalidDataException("Unterminated FName.");
    }

    public string Path(uint pointer)
    {
        var names = new List<string>();
        var seen = new HashSet<uint>();

        while (pointer != 0)
        {
            if (seen.Count == 32 || !seen.Add(pointer))
                throw new InvalidDataException("Outer cycle.");
            if (U32(pointer + 0x30) != 0)
                throw new InvalidDataException("Numbered identity is not expected here.");
            names.Add(Name(U32(pointer + 0x2C)));
            pointer = U32(pointer + 0x28);
        }

        names.Reverse();
        return string.Join('.', names);
    }

    public uint FindField(uint type, string name, string expectedPath)
    {
        var key = (type, name, expectedPath);

        if (cache != null && cache.Fields.TryGetValue(key, out FieldEntry? known))
        {
            // Caching only avoids repeated class-member scans. Classes, inheritance entries, and declarations must remain the same native objects.
            if (known.Types.All(item => Identity(item.Identity.Address) == item.Identity &&
                    U32(item.Identity.Address + 0x48) == item.Parent && U32(item.Identity.Address + 0x4C) == item.Children) &&
                Identity(known.Field.Address) == known.Field && Path(known.Field.Address) == expectedPath)
                return known.Field.Address;

            cache.Fields.TryRemove(key, out _);
        }

        var seenTypes = new HashSet<uint>();
        var types = new List<TypeEntry>();

        while (type != 0)
        {
            if (seenTypes.Count == 32 || !seenTypes.Add(type))
                throw new InvalidDataException("Class cycle.");

            QueryObject identity = Identity(type);
            uint field = U32(type + 0x4C);
            uint parent = U32(type + 0x48);
            var seenFields = new HashSet<uint>();

            types.Add(new TypeEntry(identity, parent, field));

            while (field != 0)
            {
                if (seenFields.Count == 4096 || !seenFields.Add(field) || U32(field + 0x28) != type)
                    throw new InvalidDataException("Field ownership/cycle differs.");
                if (Name(U32(field + 0x2C)) == name)
                {
                    QueryObject fieldIdentity = Identity(field);

                    if (Path(field) != expectedPath)
                        throw new InvalidDataException("Unexpected overridden field: " + Path(field));
                    if (cache != null && cache.Fields.Count < ReflectionCache.MaximumFields)
                        cache.Fields[key] = new FieldEntry(fieldIdentity, types.ToArray());

                    return field;
                }

                field = U32(field + 0x3C);
            }

            type = parent;
        }

        throw new MissingFieldException(expectedPath);
    }

    public uint FieldAddress(uint instance, string name, string expectedPath, string kind, uint size)
    {
        Identity(instance);

        uint field = FindField(U32(instance + 0x34), name, expectedPath);

        if (Path(U32(field + 0x34)) != "Core." + kind || U32(field + 0x40) != 1 || U32(field + 0x44) != size || U32(field + 0x60) + size > U32(U32(instance + 0x34) + 0x50))
            throw new InvalidDataException("Reflected field extent differs: " + expectedPath);
        return instance + U32(field + 0x60);
    }

    public bool IsA(uint instance, string typePath)
    {
        uint type = U32(instance + 0x34);
        var seen = new HashSet<uint>();

        while (type != 0)
        {
            if (!seen.Add(type) || seen.Count > 32)
                throw new InvalidDataException("Class chain cycle.");
            if (Path(type) == typePath)
                return true;
            type = U32(type + 0x48);
        }

        return false;
    }

    private sealed record NameEntry(uint Entry, uint Tag, string Text);
    private sealed record TypeEntry(QueryObject Identity, uint Parent, uint Children);
    private sealed record FieldEntry(QueryObject Field, IReadOnlyList<TypeEntry> Types);

    private sealed class ReflectionCache
    {
        public const int MaximumNames = 8192;
        public const int MaximumFields = 1024;
        public ConcurrentDictionary<uint, NameEntry> Names { get; } = new();
        public ConcurrentDictionary<(uint Type, string Name, string Path), FieldEntry> Fields { get; } = new();
    }
}

sealed record QueryObject(uint Address, uint Index, uint Outer, uint NameIndex, uint NameNumber, uint Class);
