using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Trusted loader-confirmed object identity/runtime NetIndex. Candidate package export indices cannot be
/// used directly.
/// </summary>
public sealed record Ue3PackageNetObject(long ObjectId, int RuntimeNetIndex);

/// <summary>
/// Caller-negotiated package range. NegotiatedObjectCount must come from the actual selected generation.
/// NMT_Have, matching GUIDs, or manifest order alone do not establish negotiation success.
/// </summary>
public sealed record Ue3NegotiatedPackage(long PackageId, int NegotiatedObjectCount,
    IReadOnlyList<Ue3PackageNetObject> Objects);

/// <summary>
/// Immutable static mapping based on 0x0048C8A0 base+NetIndex and 0x00486680 per-package count subtraction.
/// Uses only loader-resolved slots in the selected physical package. The caller owns dynamic Actor
/// channels, loading, level visibility, and type checks. Unresolved slots return false, not wire Null.
/// NetIndex below count cannot justify inserting an unloaded candidate.
/// </summary>
public sealed class Ue3StaticPackageMap
{
    public const int MaximumPackages = 4096;
    public const int MaximumKnownObjects = 1_000_000;
    private readonly ImmutableDictionary<long, int> objectToIndex;
    private readonly ImmutableDictionary<int, long> indexToObject;
    public long IndexExclusiveMaximum { get; }
    public int KnownObjectCount => objectToIndex.Count;

    public Ue3StaticPackageMap(IReadOnlyList<Ue3NegotiatedPackage> orderedPackages)
    {
        ArgumentNullException.ThrowIfNull(orderedPackages);
        if (orderedPackages.Count > MaximumPackages) throw new InvalidDataException("PackageMap包数量超限。");

        var forward = ImmutableDictionary.CreateBuilder<long, int>();
        var reverse = ImmutableDictionary.CreateBuilder<int, long>();
        HashSet<long> packages = [];
        HashSet<long> allObjects = [];
        long indexBase = 0;

        foreach (var package in orderedPackages)
        {
            if (package is null || package.PackageId <= 0 || !packages.Add(package.PackageId)
                || package.NegotiatedObjectCount < 0 || package.Objects is null)
                throw new InvalidDataException("PackageMap包含无效包身份、数量或空对象列表。");
            if (indexBase + package.NegotiatedObjectCount > 1L + int.MaxValue)
                throw new InvalidDataException("PackageMap范围不能由31位对象索引表示。");
            if (package.Objects.Count > MaximumKnownObjects - allObjects.Count)
                throw new InvalidDataException("PackageMap已知对象数量超限。");

            HashSet<int> localIndices = [];

            foreach (var item in package.Objects)
            {
                if (item is null || item.ObjectId <= 0 || !allObjects.Add(item.ObjectId)
                    || item.RuntimeNetIndex < -1
                    || (item.RuntimeNetIndex >= 0 && !localIndices.Add(item.RuntimeNetIndex)))
                    throw new InvalidDataException("PackageMap对象身份或运行时NetIndex无效、重复。");
                // -1 and objects beyond the negotiated generation stay outside this snapshot; never extend ranges or consume the next package's slots.
                if (item.RuntimeNetIndex < 0 || item.RuntimeNetIndex >= package.NegotiatedObjectCount) continue;

                int index = checked((int)(indexBase + item.RuntimeNetIndex));

                forward.Add(item.ObjectId, index);
                reverse.Add(index, item.ObjectId);
            }
            indexBase += package.NegotiatedObjectCount;
        }
        objectToIndex = forward.ToImmutable();
        indexToObject = reverse.ToImmutable();
        IndexExclusiveMaximum = indexBase;
    }

    public bool CanSerializeObject(long objectId) => objectToIndex.ContainsKey(objectId);
    public bool TryGetIndex(long objectId, out int index) => objectToIndex.TryGetValue(objectId, out index);
    public bool TryGetObject(int index, out long objectId) => indexToObject.TryGetValue(index, out objectId);

    public Ue3ObjectReference GetReference(long objectId)
        => objectToIndex.TryGetValue(objectId, out int index) ? new(Ue3ObjectReferenceKind.PackageObject, index)
            : throw new InvalidDataException("对象不在当前已协商的静态PackageMap内。");
}
