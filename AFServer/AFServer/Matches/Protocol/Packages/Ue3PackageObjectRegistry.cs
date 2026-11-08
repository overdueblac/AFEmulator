namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Resolved loading context. LinkerExportIndex comes from object+0x20, never synthesized from candidate
/// export ordinals. Null LinkerRootPackageFlags means no linker/root; other fields map archive+0x6C/+0x10.
/// SerializedNetIndex exists only when archive lacks 0x1000. This type neither reads resources nor confirms
/// physical-package selection.
/// </summary>
public sealed record Ue3NetIndexLoadContext(uint ArchiveFlags, bool IsLoading, int? SerializedNetIndex,
    uint? LinkerRootPackageFlags, int LinkerExportIndex);

/// <summary>
/// Index selection from 0x004EB990 and package-flag gating from 0x004EB740. Selection is not successful
/// registration.
/// </summary>
public static class Ue3RuntimeNetIndex
{
    public static int Select(int currentIndex, uint outermostPackageFlags, Ue3NetIndexLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (currentIndex < -1 || context.LinkerExportIndex < -1 || context.SerializedNetIndex is < -1)
            throw new InvalidDataException("对象加载索引必须为-1或非负Int32。");

        bool serialized = (context.ArchiveFlags & 0x1000) == 0;

        if (serialized != context.SerializedNetIndex.HasValue)
            throw new InvalidDataException("archive标志与串行NetIndex存在状态矛盾。");
        if (!serialized || !context.IsLoading) return currentIndex;

        int selected;

        if (context.LinkerRootPackageFlags is uint flags && (flags & 8) == 0)
        {
            // Native linker index -1 skips SetNetIndex and retains the previous value, without falling back to serialized data.
            if (context.LinkerExportIndex == -1) return currentIndex;
            selected = context.LinkerExportIndex;
        }
        else selected = context.SerializedNetIndex!.Value;
        return (outermostPackageFlags & 4) == 0 ? selected : currentIndex;
    }
}

/// <summary>
/// Resolved-object registry for one selected physical package, following
/// SetNetIndex/AddNetObject/RemoveNetObject. Capacity is the actual package-object array length, separate
/// from negotiated generation counts. Allocates no guessed slots and executes no loader/notification
/// callbacks. Caller use is serialized; reject invalid input before mutation and preserve immutable
/// snapshots.
/// </summary>
public sealed class Ue3PackageObjectRegistry
{
    public const int MaximumKnownObjects = Ue3StaticPackageMap.MaximumKnownObjects;
    private readonly Dictionary<long, int> objects = [];
    private readonly Dictionary<int, long> slots = [];
    public long PackageId { get; }
    public uint PackageFlags { get; }
    public int ObjectSlotCount { get; }
    public int KnownObjectCount => objects.Count;
    public int RegisteredObjectCount => slots.Count;

    /// <summary>
    /// initialObjects must be loader-confirmed current identities/slots; -1 and sparse holes are allowed.
    /// </summary>
    public Ue3PackageObjectRegistry(long packageId, uint packageFlags, int objectSlotCount,
        IReadOnlyList<Ue3PackageNetObject> initialObjects)
    {
        ArgumentNullException.ThrowIfNull(initialObjects);
        if (packageId <= 0 || objectSlotCount < 0 || initialObjects.Count > MaximumKnownObjects)
            throw new InvalidDataException("包身份、实际槽容量或对象数量无效。");
        PackageId = packageId;
        PackageFlags = packageFlags;
        ObjectSlotCount = objectSlotCount;
        foreach (var item in initialObjects)
        {
            if (item is null || item.ObjectId <= 0 || !objects.TryAdd(item.ObjectId, item.RuntimeNetIndex))
                throw new InvalidDataException("已解析对象身份无效或重复。");
            ValidateIndex(item.RuntimeNetIndex);
            if (item.RuntimeNetIndex >= 0 && !slots.TryAdd(item.RuntimeNetIndex, item.ObjectId))
                throw new InvalidDataException("已解析对象槽重复。");
        }
    }

    /// <summary>
    /// Declares an identity-resolved but unregistered object without granting network serializability.
    /// </summary>
    public void TrackResolvedObject(long objectId)
    {
        if (objectId <= 0 || objects.ContainsKey(objectId) || objects.Count >= MaximumKnownObjects)
            throw new InvalidDataException("已解析对象身份无效、重复或数量超限。");
        objects.Add(objectId, -1);
    }

    /// <summary>
    /// Applies one serialized NetIndex result. Native code logs invalid slots; C# atomically rejects the
    /// operation so object indices and package slots cannot diverge. Returns committed runtime NetIndex.
    /// </summary>
    public int ApplyLoadedIndex(long objectId, Ue3NetIndexLoadContext context)
    {
        if (!objects.TryGetValue(objectId, out int previous))
            throw new InvalidDataException("对象尚未完成身份解析。");

        int selected = Ue3RuntimeNetIndex.Select(previous, PackageFlags, context);

        if (selected == previous) return previous;
        ValidateIndex(selected);
        if (selected >= 0 && slots.ContainsKey(selected))
            throw new InvalidDataException("运行时NetIndex目标槽已被另一对象占用。");
        if (previous >= 0) slots.Remove(previous);
        if (selected >= 0) slots.Add(selected, objectId);
        objects[objectId] = selected;
        return selected;
    }

    public bool TryGetObject(int runtimeIndex, out long objectId) => slots.TryGetValue(runtimeIndex, out objectId);
    public bool TryGetRuntimeIndex(long objectId, out int runtimeIndex) => objects.TryGetValue(objectId, out runtimeIndex);

    /// <summary>
    /// Immutable object snapshot for this physical package's negotiation layer. Each connection determines
    /// its negotiated range separately.
    /// </summary>
    public IReadOnlyList<Ue3PackageNetObject> CaptureObjects()
        => Array.AsReadOnly(objects.OrderBy(x => x.Key).Select(x => new Ue3PackageNetObject(x.Key, x.Value)).ToArray());

    private void ValidateIndex(int index)
    {
        if (index < -1 || index >= ObjectSlotCount)
            throw new InvalidDataException("运行时NetIndex超出实际包对象数组容量。");
    }
}
