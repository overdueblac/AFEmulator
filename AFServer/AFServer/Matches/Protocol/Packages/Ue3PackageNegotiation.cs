using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// One physical package selected by a trusted loader. Announcement contains caller-chosen Uses fields;
/// generations are runtime NetObjectCounts. Objects must be resolved runtime slots, never export indices.
/// This component does not infer package selection, physical-name disambiguation, or meanings of three
/// unknown Uses fields.
/// </summary>
public sealed record Ue3PackageNegotiationPackage(long PackageId, Ue3PackageAnnouncement Announcement,
    IReadOnlyList<int> GenerationNetObjectCounts, IReadOnlyList<Ue3PackageNetObject> Objects);

public enum Ue3PackageNegotiationState { AwaitingHave, Available, UnloadRequested, Unloaded }

/// <summary>
/// Package range in one snapshot. Unloaded packages retain ranges but expose no resolved objects.
/// </summary>
public sealed record Ue3PackageNegotiationRange(long PackageId, Ue3PackageGuid Guid,
    Ue3PackageNegotiationState State, int LocalGeneration, int RemoteGeneration, long BaseIndex, int ObjectCount);

/// <summary>
/// Use PackageMap and ClassNetCache from the same Revision together, rebuilding both after state changes.
/// </summary>
public sealed record Ue3PackageNegotiationSnapshot(long Revision,
    IReadOnlyList<Ue3PackageNegotiationRange> Ranges, Ue3StaticPackageMap PackageMap);

/// <summary>
/// Single-connection, single-thread server package-negotiation prelude. Input order is selected Uses order;
/// never sorts or reads client files. Handles registered Have/Skip and Have/Abort following explicit unload
/// requests only. Implements no downloads, authentication, or automatic match joining. Range algorithm
/// follows 0x00495A50; capacity/unsupported-input rejection are implementation boundaries.
/// </summary>
public sealed class Ue3PackageNegotiation
{
    public const int MaximumGenerationsPerPackage = 4096;
    public const int MaximumTotalGenerationEntries = 65536;
    private sealed class Entry(Ue3PackageNegotiationPackage package)
    {
        internal readonly long Id = package.PackageId;
        internal readonly Ue3PackageAnnouncement Announcement = package.Announcement;
        internal readonly ImmutableArray<int> Counts = package.GenerationNetObjectCounts.ToImmutableArray();
        internal readonly ImmutableArray<Ue3PackageNetObject> Objects = package.Objects.ToImmutableArray();
        internal Ue3PackageNegotiationState State = Ue3PackageNegotiationState.AwaitingHave;
        internal int RemoteGeneration;
        internal int ObjectCount;
    }

    private readonly List<Entry> ordered = [];
    private readonly Dictionary<Ue3PackageGuid, Entry> byGuid = [];
    public long Revision { get; private set; }
    public int PackageCount => ordered.Count;
    /// <summary>
    /// Means only that retained packages received positive-generation Have, not full slot resolution or
    /// match readiness.
    /// </summary>
    public bool CanCaptureSnapshot => ordered.All(x => x.State != Ue3PackageNegotiationState.AwaitingHave);

    public Ue3PackageNegotiation(IReadOnlyList<Ue3PackageNegotiationPackage> orderedPackages)
    {
        ArgumentNullException.ThrowIfNull(orderedPackages);
        if (orderedPackages.Count > Ue3StaticPackageMap.MaximumPackages)
            throw new InvalidDataException("包协商清单数量超限。");

        int totalGenerations = 0;
        int totalObjects = 0;
        HashSet<long> ids = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (var package in orderedPackages)
        {
            if (package is null || package.PackageId <= 0 || !ids.Add(package.PackageId)
                || package.Announcement is null || package.GenerationNetObjectCounts is null || package.Objects is null)
                throw new InvalidDataException("包协商输入含空值、无效或重复物理包身份。");

            var announcement = package.Announcement;

            if (string.IsNullOrWhiteSpace(announcement.PackageName) || !names.Add(announcement.PackageName)
                || byGuid.ContainsKey(announcement.Guid))
                throw new InvalidDataException("包协商要求调用方先消除重复名称或 GUID 的物理包歧义。");
            if (package.Objects.Count > Ue3StaticPackageMap.MaximumKnownObjects - totalObjects)
                throw new InvalidDataException("包协商已知对象总数超限。");

            int generations = package.GenerationNetObjectCounts.Count;

            if (generations is < 1 or > MaximumGenerationsPerPackage
                || generations > MaximumTotalGenerationEntries - totalGenerations
                || package.GenerationNetObjectCounts.Any(x => x < 0)
                || announcement.RemoteGeneration != generations)
                throw new InvalidDataException("包代际表无效、超限或 Uses 声明代际与已加载本地表不一致。");
            // Reuse actual-field encoding limits. The caller must still explicitly supply unknown extension fields.
            _ = Ue3ControlCodec.EncodePackageAnnouncement(announcement);
            totalGenerations += generations;
            totalObjects += package.Objects.Count;

            var entry = new Entry(package);

            ordered.Add(entry);
            byGuid.Add(announcement.Guid, entry);
        }
        // Validate every slot identity, duplicate, and maximum negotiated range before control messages alter state.
        _ = new Ue3StaticPackageMap(ordered.Select(x => new Ue3NegotiatedPackage(x.Id, x.Counts[^1], x.Objects)).ToArray());
    }

    /// <summary>
    /// Returns a copy of caller-provided Uses announcements, without sending packets or registering new
    /// physical packages automatically.
    /// </summary>
    public IReadOnlyList<Ue3PackageAnnouncement> CaptureAnnouncements()
        => ordered.Select(x => x.Announcement).ToImmutableArray();

    /// <summary>
    /// Accepts positive one-based generations. Newer remote generations use the final local range; older
    /// ones use min(final local count, corresponding remote count). Native code permits zero and retains
    /// the previous count; this server explicitly rejects nonpositive wire Have to avoid false negotiation
    /// completion.
    /// </summary>
    public void ApplyHave(Ue3PackageHave message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Entry entry = Find(message.Guid);

        if (message.RemoteGeneration <= 0) throw new InvalidDataException("Have 代际必须为正的一基值。");
        if (entry.State == Ue3PackageNegotiationState.Unloaded)
            throw new InvalidDataException("已卸载的包必须经新的显式加载流程重新登记。");

        int count = entry.Counts[^1];

        if (message.RemoteGeneration < entry.Counts.Length)
            count = Math.Min(count, entry.Counts[message.RemoteGeneration - 1]);
        if (entry.State == Ue3PackageNegotiationState.UnloadRequested)
        {
            // Native Have writes generation and calls Compute before processing pending unloads. Retain the range from that calculation.
            entry.ObjectCount = count;
            CompleteUnload(entry);
            return;
        }
        if (entry.State == Ue3PackageNegotiationState.Available && entry.RemoteGeneration == message.RemoteGeneration) return;
        entry.RemoteGeneration = message.RemoteGeneration;
        entry.ObjectCount = count;
        entry.State = Ue3PackageNegotiationState.Available;
        Revision++;
    }

    /// <summary>
    /// NMT_Skip removes a matching package from the ordered list. Later bases recalculate in the next
    /// snapshot, without fabricating Have.
    /// </summary>
    public void ApplySkip(Ue3PackageSkip message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Entry entry = Find(message.Guid);

        if (entry.State is Ue3PackageNegotiationState.UnloadRequested or Ue3PackageNegotiationState.Unloaded)
            throw new InvalidDataException("不能用 Skip 完成卸载事务。");
        ordered.Remove(entry);
        byGuid.Remove(message.Guid);
        Revision++;
    }

    /// <summary>
    /// Explicitly registers a pending-unload GUID; the caller sends returned NMT_Unload reliably. Server
    /// policy immediately revokes object availability in new snapshots while retaining allocated
    /// ObjectCount. Only matching Have/Abort ends this wait.
    /// </summary>
    public Ue3PackageUnload RequestUnload(Ue3PackageGuid guid)
    {
        Entry entry = Find(guid);

        if (entry.State != Ue3PackageNegotiationState.Available)
            throw new InvalidDataException("只能卸载已完成 Have 的包。");
        entry.State = Ue3PackageNegotiationState.UnloadRequested;
        Revision++;
        return new Ue3PackageUnload(guid);
    }

    /// <summary>
    /// Accepts Abort only for this connection's matching pending-unload GUID. Without a pending unload,
    /// Abort cannot mean success.
    /// </summary>
    public void ApplyAbort(Ue3PackageAbort message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Entry entry = Find(message.Guid);

        if (entry.State != Ue3PackageNegotiationState.UnloadRequested)
            throw new InvalidDataException("Abort 没有匹配的待卸载包。");
        CompleteUnload(entry);
    }

    /// <summary>
    /// Accepts only three recovered inbound messages. Uses/Unload travel in the opposite direction and are
    /// not implicitly applied here.
    /// </summary>
    public void ApplyClientMessage(Ue3ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case Ue3PackageHave have: ApplyHave(have); break;
            case Ue3PackageSkip skip: ApplySkip(skip); break;
            case Ue3PackageAbort abort: ApplyAbort(abort); break;
            default: throw new InvalidDataException($"包协商不支持客户端控制消息 {message.MessageType}。");
        }
    }

    /// <summary>
    /// Rejects snapshots while any package lacks Have. Unloaded packages retain identical slot holes to
    /// prevent accidental global-index reuse.
    /// </summary>
    public Ue3PackageNegotiationSnapshot CaptureSnapshot()
    {
        if (!CanCaptureSnapshot) throw new InvalidDataException("仍有包等待 Have，不能构造已协商 PackageMap。");

        long indexBase = 0;
        var ranges = ImmutableArray.CreateBuilder<Ue3PackageNegotiationRange>(ordered.Count);
        var packages = new List<Ue3NegotiatedPackage>(ordered.Count);

        foreach (var entry in ordered)
        {
            ranges.Add(new(entry.Id, entry.Announcement.Guid, entry.State, entry.Counts.Length,
                entry.RemoteGeneration, indexBase, entry.ObjectCount));
            packages.Add(new(entry.Id, entry.ObjectCount,
                entry.State == Ue3PackageNegotiationState.Available ? entry.Objects : ImmutableArray<Ue3PackageNetObject>.Empty));
            indexBase += entry.ObjectCount;
        }
        return new(Revision, ranges.MoveToImmutable(), new Ue3StaticPackageMap(packages));
    }

    private Entry Find(Ue3PackageGuid guid) => byGuid.TryGetValue(guid, out var entry) ? entry
        : throw new InvalidDataException($"控制消息 GUID {guid.A:X8}{guid.B:X8}{guid.C:X8}{guid.D:X8} 未登记。");

    private void CompleteUnload(Entry entry)
    {
        entry.State = Ue3PackageNegotiationState.Unloaded;
        entry.RemoteGeneration = 0;
        Revision++;
    }
}
