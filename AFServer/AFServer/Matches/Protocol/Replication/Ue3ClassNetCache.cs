namespace AFServer.AssaultFire.Matches.Protocol;

public enum Ue3NetFieldKind { Property, Function }

/// <summary>
/// Resolved runtime UField metadata. ObjectId is a unique local identity, never a logical path, export
/// index, or wire ID. RuntimeNetIndex maps UObject+0x24; Flags maps property+0x48/function+0xAC. Uses only
/// proven network flag bits.
/// </summary>
public sealed record Ue3RuntimeNetField(long ObjectId, int RuntimeNetIndex, Ue3NetFieldKind Kind, ulong Flags)
{
    public bool FunctionHasSuper { get; init; }
    /// <summary>Runtime property+0x74. Null means an actual null pointer, not unresolved data.</summary>
    public long? ReplicationOwnerId { get; init; }
    /// <summary>Owning UClass+0xF4. Bit 0x800 controls the cache's property list, not field IDs.</summary>
    public uint OwnerClassFlags { get; init; }
}

/// <summary>
/// Zero-based cache FieldId, used directly by the verified Actor receive path. Zero is not a terminator.
/// </summary>
public sealed record Ue3FieldNetCache(Ue3RuntimeNetField Field, int FieldId, int ConditionIndex);

/// <summary>
/// Bounded immutable AF2014 field-cache core based on 0x0049E110, 0x0046CED0, 0x00499E00, and 0x0067DA80.
/// Requires actual same-PackageMap metadata, correct Super caches, and deterministic CanSerializeObject.
/// Reads no client files, infers no UObject identities, executes no replication/RPC, and synthesizes no
/// unknown checksum.
/// </summary>
public sealed class Ue3ClassNetCache
{
    // A server resource limit, not a claimed recovered native constant.
    public const int MaximumFields = 65536;
    public const int MaximumInheritanceDepth = 256;

    private readonly Dictionary<long, int> localByObject;
    private readonly Ue3FieldNetCache[] localFields;

    public long ClassObjectId { get; }
    public Ue3ClassNetCache? SuperCache { get; }
    public int FieldIdBase { get; }
    public int FieldIdExclusiveMaximum { get; }
    public int ConditionCount { get; }
    public int InheritanceDepth { get; }
    public IReadOnlyList<Ue3FieldNetCache> LocalFields { get; }
    public IReadOnlyList<Ue3FieldNetCache> ReplicatedProperties { get; }

    private Ue3ClassNetCache(long classObjectId, Ue3ClassNetCache? superCache,
        Ue3FieldNetCache[] fields, int conditionCount, Dictionary<long, int> byObject)
    {
        ClassObjectId = classObjectId;
        SuperCache = superCache;
        FieldIdBase = superCache?.FieldIdExclusiveMaximum ?? 0;
        FieldIdExclusiveMaximum = checked(FieldIdBase + fields.Length);
        ConditionCount = conditionCount;
        InheritanceDepth = (superCache?.InheritanceDepth ?? 0) + 1;
        localFields = fields;
        LocalFields = Array.AsReadOnly(fields);
        localByObject = byObject;
        ReplicatedProperties = Array.AsReadOnly((superCache?.ReplicatedProperties ?? [])
            .Concat(fields.Where(x => x.Field.Kind == Ue3NetFieldKind.Property
                && (x.Field.OwnerClassFlags & 0x800) == 0)).ToArray());
    }

    /// <summary>
    /// localChildren contains only this UClass's local Children, never repeated inherited fields. false
    /// corresponds to native GetClassNetCache returning null. Cached parents must share PackageMap state.
    /// Uncertain duplicate NetIndex, wrong identities, missing data, or unresolved conditional aliases
    /// throw rather than receive guessed IDs.
    /// </summary>
    public static Ue3ClassNetCache? Build(long classObjectId, IReadOnlyList<Ue3RuntimeNetField> localChildren,
        Ue3ClassNetCache? superCache, Func<long, bool> canSerializeObject)
    {
        ArgumentNullException.ThrowIfNull(localChildren);
        ArgumentNullException.ThrowIfNull(canSerializeObject);
        if (classObjectId <= 0) throw new ArgumentOutOfRangeException(nameof(classObjectId));
        if (localChildren.Count > MaximumFields) throw new InvalidDataException("本地字段数量超出服务端上限。");
        if ((superCache?.InheritanceDepth ?? 0) >= MaximumInheritanceDepth)
            throw new InvalidDataException("字段缓存继承层数超出服务端上限。");

        HashSet<long> classIdentities = [classObjectId];

        for (var parent = superCache; parent is not null; parent = parent.SuperCache)
            if (!classIdentities.Add(parent.ClassObjectId) || parent.localByObject.ContainsKey(classObjectId))
                throw new InvalidDataException("字段缓存类身份与继承链中的类或字段重复。");

        // Native construction queries the same object repeatedly. Evaluate once per snapshot so stateful callbacks cannot produce contradictory IDs.
        Dictionary<long, bool> serialization = [];

        bool CanSerialize(long id)
        {
            if (!serialization.TryGetValue(id, out bool accepted))
                serialization.Add(id, accepted = canSerializeObject(id));
            return accepted;
        }
        if (!CanSerialize(classObjectId)) return null;

        HashSet<long> identities = [];
        List<Ue3RuntimeNetField> netFields = [];

        foreach (var field in localChildren)
        {
            if (field is null || field.ObjectId <= 0 || classIdentities.Contains(field.ObjectId)
                || !identities.Add(field.ObjectId) || superCache?.FindField(field.ObjectId) is not null
                || field.RuntimeNetIndex < -1 || !Enum.IsDefined(field.Kind)
                || field.ReplicationOwnerId is <= 0
                || (field.Kind == Ue3NetFieldKind.Function && field.Flags > uint.MaxValue))
                throw new InvalidDataException("运行时字段元数据包含无效或重复身份、索引或类型。");

            bool network = field.Kind == Ue3NetFieldKind.Property
                ? (field.Flags & 0x20) != 0
                : (field.Flags & 0x40) != 0 && !field.FunctionHasSuper;

            if (network) netFields.Add(field);
        }
        // The native comparator is signed(keyA-keyB), not overflow-safe comparison. Reject any potentially overflowing sort input, including later-filtered fields.
        if (netFields.Count > 0 && (long)netFields.Max(x => x.RuntimeNetIndex) - netFields.Min(x => x.RuntimeNetIndex) > int.MaxValue)
            throw new InvalidDataException("NetIndex 排序差值超出已支持的原生比较范围。");

        // Native code sorts NetFields before CanSerialize filtering and does not deduplicate by name.
        var acceptedFields = netFields.OrderBy(x => x.RuntimeNetIndex).Where(x => CanSerialize(x.ObjectId)).ToArray();
        int fieldBase = superCache?.FieldIdExclusiveMaximum ?? 0;

        if (acceptedFields.Length > MaximumFields - fieldBase)
            throw new InvalidDataException("字段缓存总数量超出服务端上限。");
        for (int i = 0; i < acceptedFields.Length; i++)
            if (acceptedFields[i].RuntimeNetIndex < 0
                || (i > 0 && acceptedFields[i - 1].RuntimeNetIndex == acceptedFields[i].RuntimeNetIndex))
                throw new InvalidDataException("可序列化字段的 NetIndex 无效或存在尚未支持的相同排序键。");

        var fields = new Ue3FieldNetCache[acceptedFields.Length];
        Dictionary<long, int> byObject = [];
        int conditionCount = superCache?.ConditionCount ?? 0;

        for (int i = 0; i < fields.Length; i++)
        {
            var field = acceptedFields[i];
            int condition = -1;

            if (field.Kind == Ue3NetFieldKind.Property
                && (field.ReplicationOwnerId is not long owner || owner == field.ObjectId || !CanSerialize(owner)))
                condition = conditionCount++;
            fields[i] = new(field, fieldBase + i, condition);
            byObject.Add(field.ObjectId, i);
        }
        // 0x0049A0AC backfills aliases once in sorted order. Do not recursively repair alias chains left unresolved natively.
        for (int i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];

            if (entry.Field.Kind != Ue3NetFieldKind.Property || entry.ConditionIndex >= 0) continue;

            long owner = entry.Field.ReplicationOwnerId!.Value;
            var ownerEntry = byObject.TryGetValue(owner, out int localIndex)
                ? fields[localIndex] : superCache?.FindField(owner);

            if (ownerEntry is null || ownerEntry.ConditionIndex < 0)
                throw new InvalidDataException("可序列化的复制条件别名没有已解决的 property 缓存。");
            fields[i] = entry with { ConditionIndex = ownerEntry.ConditionIndex };
        }
        return new Ue3ClassNetCache(classObjectId, superCache, fields, conditionCount, byObject);
    }

    public Ue3FieldNetCache? FindField(long objectId)
    {
        for (var cache = this; cache is not null; cache = cache.SuperCache)
            if (cache.localByObject.TryGetValue(objectId, out int index)) return cache.localFields[index];
        return null;
    }

    public Ue3FieldNetCache? FindFieldById(int fieldId)
    {
        if (fieldId < 0 || fieldId >= FieldIdExclusiveMaximum) return null;
        for (var cache = this; cache is not null; cache = cache.SuperCache)
            if (fieldId >= cache.FieldIdBase && fieldId < cache.FieldIdExclusiveMaximum)
                return cache.localFields[fieldId - cache.FieldIdBase];
        return null;
    }

    /// <summary>
    /// Writes zero-based FieldId directly, with exclusive bound parent-count plus local-count. No increment
    /// or zero terminator, following Actor receiver 0x011F8E60 and pass-through wrapper 0x0045CB00. Uses
    /// adaptive native SerializeInt; non-power-of-two ranges do not use fixed ceil(log2(Max)) width.
    /// </summary>
    public void WriteFieldId(LsbBitWriter writer, long fieldObjectId)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var entry = FindField(fieldObjectId) ?? throw new InvalidDataException("对象不在当前字段缓存中。");

        writer.WriteSerializedInt(entry.FieldId, FieldIdExclusiveMaximum);
    }

    /// <summary>
    /// Reads one field reference without deciding Actor-bunch termination. ID=0 in a single-field cache
    /// consumes zero bits; consumption cannot identify list end, and field zero is not a terminator.
    /// </summary>
    public Ue3FieldNetCache ReadField(ref LsbBitReader reader)
    {
        if (FieldIdExclusiveMaximum == 0) throw new InvalidDataException("空字段缓存不能读取 field ID。");

        var cursor = reader;
        int id = cursor.ReadSerializedInt(FieldIdExclusiveMaximum);
        var entry = FindFieldById(id) ?? throw new InvalidDataException("字段编号不在继承缓存中。");

        reader = cursor;
        return entry;
    }
}
