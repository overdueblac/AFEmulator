using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AFServer.AssaultFire.Matches.Protocol;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Physical packages are caller-selected. PackageId is a server identity, not a network index or Uses
/// order.
/// </summary>
public sealed record Ue3PhysicalPackageSelection(long PackageId, string PhysicalPackage);

/// <summary>
/// Out-of-band pinned research-artifact fingerprints. An input manifest cannot establish its own trust.
/// </summary>
public sealed record Ue3ObjectSourceTrust(string PackageManifestSha256, string ActorSchemaManifestSha256,
    string ClassDefaultsManifestSha256);

/// <summary>Preserves FName numeric suffixes. Formatted names cannot replace identity.</summary>
public sealed record Ue3ObjectName(string Text, int Number);

/// <summary>
/// Logical serial range in the original physical package. Compressed-package logical offsets are not disk
/// offsets.
/// </summary>
public sealed record Ue3ObjectSourceLocation(string PhysicalPackage, string PackageSha256,
    int LogicalOffset, int Bytes, string? SerialSha256);

/// <summary>
/// Reference resolution within explicitly selected physical packages, creating no placeholders for
/// missing/ambiguous references.
/// </summary>
public enum Ue3ReferenceResolutionStatus
{
    /// <summary>The original package reference is zero and has no target.</summary>
    Null,
    /// <summary>Uniquely resolved within the selected physical set.</summary>
    Resolved,
    /// <summary>No object has the corresponding complete logical path.</summary>
    Missing,
    /// <summary>Multiple physical objects match the complete path and declared type.</summary>
    Ambiguous,
    /// <summary>Objects share the path but none matches the import's declared type.</summary>
    TypeMismatch
}

/// <summary>
/// Retains the full FName path, raw package reference, and number of declared-type-compatible candidates.
/// </summary>
public sealed record Ue3ReferenceResolution(int PackageReference, ImmutableArray<Ue3ObjectName> LogicalPath,
    Ue3ReferenceResolutionStatus Status, Ue3ExportIdentity? Target, int MatchingCandidates);

/// <summary>
/// Resolved physical export identity, without creating Actors, executing constructors, or synthesizing
/// effective defaults.
/// </summary>
public sealed record Ue3ExportIdentity(long ObjectId, long PackageId, int ExportIndex,
    ImmutableArray<Ue3ObjectName> LogicalPath, int ClassReference, int SuperReference, int OuterReference,
    int ArchetypeReference, ulong ObjectFlags, int? SerializedNetIndex)
{
    public string DisplayPath => string.Join('.', LogicalPath.Select(x => x.Text + (x.Number == 0 ? "" : $"_{x.Number - 1}")));
}

/// <summary>
/// Class declaration confirmed by bidirectional CDO evidence. ExplicitDefaultTags are explicit only, never
/// effective runtime defaults.
/// </summary>
public sealed record Ue3ClassIdentity(Ue3ExportIdentity Definition, Ue3ExportIdentity DefaultObject,
    ImmutableArray<JsonElement> ExplicitDefaultTags);

/// <summary>
/// Object catalog/class identities for explicitly selected physical packages. Imports resolve by complete
/// Outer/FName chains; missing native objects or multiple identities fail. Research JSON retains candidate
/// boundaries. Starts no match and never treats candidate export ordinals as NetIndex.
/// </summary>
public sealed class Ue3ObjectIdentityCatalog
{
    internal sealed class PackageData
    {
        internal required Ue3PhysicalPackageSelection Selection;
        internal required uint Flags;
        internal required ImmutableArray<int> Generations;
        internal required Ue3PackageGuid Guid;
        internal required string SourceSha256;
        internal required ImmutableArray<string> Names;
        internal required ImmutableArray<Ue3ObjectName>[] ImportPaths;
        internal required (string Package, string Class, int PackageNumber, int ClassNumber)[] ImportTypes;
        internal required ImmutableArray<Ue3ExportIdentity> Exports;
        internal required ImmutableArray<Ue3ObjectSourceLocation> Sources;
        internal readonly Dictionary<int, Ue3ClassIdentity> Classes = [];
        internal readonly Dictionary<int, Ue3ClassIdentity> DefaultClasses = [];
    }

    private readonly ImmutableArray<PackageData> ordered;
    private readonly Dictionary<long, PackageData> packages;
    private readonly Dictionary<string, List<Ue3ExportIdentity>> paths = new(StringComparer.Ordinal);
    public int PackageCount => ordered.Length;
    public int ExportCount => ordered.Sum(x => x.Exports.Length);
    public int VerifiedClassCount => ordered.Sum(x => x.Classes.Count);

    internal Ue3ObjectIdentityCatalog(IEnumerable<PackageData> selected)
    {
        ordered = selected.ToImmutableArray();
        packages = ordered.ToDictionary(x => x.Selection.PackageId);
        foreach (var package in ordered)
        foreach (var item in package.Exports)
        {
            string key = PathKey(item.LogicalPath);

            if (!paths.TryGetValue(key, out var candidates)) paths.Add(key, candidates = []);
            candidates.Add(item);
        }
        // Same-named objects of different classes may coexist in one physical package; declared types disambiguate reference matches.
    }

    public ImmutableArray<Ue3PhysicalPackageSelection> CaptureSelection() => ordered.Select(x => x.Selection).ToImmutableArray();
    public ImmutableArray<Ue3ExportIdentity> CaptureExports(long packageId) => GetPackage(packageId).Exports;
    public ImmutableArray<int> GetGenerationNetObjectCounts(long packageId) => GetPackage(packageId).Generations;
    public Ue3PackageGuid GetPackageGuid(long packageId) => GetPackage(packageId).Guid;
    public uint GetPackageFlags(long packageId) => GetPackage(packageId).Flags;
    public string GetPackageSourceSha256(long packageId) => GetPackage(packageId).SourceSha256;

    /// <summary>
    /// Restores FName through a trusted physical-package name table, preserving raw number without deriving
    /// indices from display names.
    /// </summary>
    public Ue3ObjectName ResolvePackageName(long packageId, int nameIndex, int number)
    {
        var names = GetPackage(packageId).Names;

        if (nameIndex < 0 || nameIndex >= names.Length || number < 0)
            throw new InvalidDataException("FName 索引或 number 超出可信物理包名称表。");

        return new(names[nameIndex], number);
    }

    /// <summary>
    /// Returns sources checked against directory/schema/CDO evidence. Ordinary exports without extracted
    /// serial hashes remain null.
    /// </summary>
    public Ue3ObjectSourceLocation GetExportSource(long packageId, int exportIndex)
    {
        _ = GetExport(packageId, exportIndex);
        return GetPackage(packageId).Sources[exportIndex - 1];
    }

    /// <summary>
    /// Queries classes supported by bidirectional CDO evidence. Ordinary objects return false; invalid
    /// physical identities still fail.
    /// </summary>
    public bool TryGetClass(long packageId, int exportIndex, out Ue3ClassIdentity? identity)
    {
        _ = GetExport(packageId, exportIndex);
        return GetPackage(packageId).Classes.TryGetValue(exportIndex, out identity);
    }

    /// <summary>Checks whether an export is a verified CDO without guessing from name or flags alone.</summary>
    public bool TryGetDefaultObjectClass(long packageId, int exportIndex, out Ue3ClassIdentity? identity)
    {
        _ = GetExport(packageId, exportIndex);
        return GetPackage(packageId).DefaultClasses.TryGetValue(exportIndex, out identity);
    }

    public Ue3ExportIdentity GetExport(long packageId, int exportIndex)
    {
        var package = GetPackage(packageId);

        if (exportIndex < 1 || exportIndex > package.Exports.Length) throw new InvalidDataException("export 身份超出已选物理包。");
        return package.Exports[exportIndex - 1];
    }

    /// <summary>
    /// Queries complete FName chains and rejects logical-path duplicates across packages. The caller must
    /// explicitly reselect physical packages.
    /// </summary>
    public Ue3ExportIdentity ResolveUniqueLogicalPath(IReadOnlyList<Ue3ObjectName> logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        if (logicalPath.Count is < 1 or > 128 || logicalPath.Any(x => x is null
            || string.IsNullOrEmpty(x.Text) || x.Number < 0))
            throw new InvalidDataException("逻辑对象路径含无效 FName。");
        if (!paths.TryGetValue(PathKey(logicalPath.ToImmutableArray()), out var matches) || matches.Count != 1)
            throw new InvalidDataException("逻辑对象路径缺失或对应多个物理包身份。");
        return matches[0];
    }

    /// <summary>
    /// Zero references return null. Never auto-loads missing packages or creates native-class/import
    /// placeholders.
    /// </summary>
    public Ue3ExportIdentity? ResolveReference(long packageId, int reference)
    {
        var resolution = InspectReference(packageId, reference);

        return resolution.Status switch
        {
            Ue3ReferenceResolutionStatus.Null => null,
            Ue3ReferenceResolutionStatus.Resolved => resolution.Target,
            _ => throw new InvalidDataException($"导入 {Display(resolution.LogicalPath)}：{resolution.Status}；"
                + $"符合目录类型的物理候选 {resolution.MatchingCandidates} 个，需要显式物理包或 native 身份证据。")
        };
    }

    /// <summary>
    /// Diagnoses only missing/ambiguous references. Invalid references still throw; results grant no
    /// runtime/network eligibility.
    /// </summary>
    public Ue3ReferenceResolution InspectReference(long packageId, int reference)
    {
        var package = GetPackage(packageId);

        if (reference == 0) return new(reference, [], Ue3ReferenceResolutionStatus.Null, null, 0);
        if (reference > 0)
        {
            var target = GetExport(packageId, reference);

            return new(reference, target.LogicalPath, Ue3ReferenceResolutionStatus.Resolved, target, 1);
        }
        if (reference == int.MinValue || -reference > package.ImportPaths.Length)
            throw new InvalidDataException("import 引用超出物理包目录。");

        int index = -reference - 1;
        string key = PathKey(package.ImportPaths[index]);

        if (!paths.TryGetValue(key, out var candidates))
            return new(reference, package.ImportPaths[index], Ue3ReferenceResolutionStatus.Missing, null, 0);

        // Import class-package/class-name are type constraints, not just lookup hints.
        var type = package.ImportTypes[index];
        var matching = candidates.Where(x => HasDeclaredType(x, type)).ToArray();
        var status = matching.Length switch
        {
            0 => Ue3ReferenceResolutionStatus.TypeMismatch,
            1 => Ue3ReferenceResolutionStatus.Resolved,
            _ => Ue3ReferenceResolutionStatus.Ambiguous
        };

        return new(reference, package.ImportPaths[index], status, matching.Length == 1 ? matching[0] : null, matching.Length);
    }

    public Ue3ClassIdentity GetClass(long packageId, int exportIndex)
    {
        var package = GetPackage(packageId);

        return package.Classes.TryGetValue(exportIndex, out var value) ? value
            : throw new InvalidDataException("对象没有 class/CDO 双向证据；zero-class-reference 本身不足以证明类身份。");
    }

    /// <summary>
    /// Returns the definite inheritance chain to the root, rejecting missing/nonclass bases and cycles
    /// rather than silently truncating.
    /// </summary>
    public ImmutableArray<Ue3ClassIdentity> ResolveClassAncestry(long packageId, int exportIndex)
    {
        var result = ImmutableArray.CreateBuilder<Ue3ClassIdentity>();
        HashSet<long> visited = [];
        var current = GetClass(packageId, exportIndex);

        while (true)
        {
            if (result.Count >= 128 || !visited.Add(current.Definition.ObjectId))
                throw new InvalidDataException("类继承循环或深度超过 128。");
            result.Add(current);
            if (current.Definition.SuperReference == 0) return result.ToImmutable();

            var parent = ResolveReference(current.Definition.PackageId, current.Definition.SuperReference)!;

            current = GetClass(parent.PackageId, parent.ExportIndex);
        }
    }

    public Ue3ClassIdentity ResolveObjectClass(long packageId, int exportIndex)
    {
        var item = GetExport(packageId, exportIndex);

        if (item.ClassReference == 0)
            throw new InvalidDataException("没有恢复 zero-class-reference 的元类加载分支；不能把它自动映射到 Core.Class。");

        var definition = ResolveReference(packageId, item.ClassReference)!;
        var ancestry = ResolveClassAncestry(definition.PackageId, definition.ExportIndex);

        return ancestry[0];
    }

    /// <summary>
    /// Registers resolved identities with extracted serialized NetIndex through the existing core. Capacity
    /// comes explicitly from a trusted caller. Supports only the proven linker-root flags&amp;8 serial
    /// branch; embedded Package, linker export-slot paths, and unknown serial values fail. Returns a new
    /// table atomically without leaking partial batches. Success proves neither native construction nor
    /// Actor activation.
    /// </summary>
    public Ue3PackageObjectRegistry CreateSerializedIdentityRegistry(long packageId, int objectSlotCount,
        IReadOnlyList<int> exportIndices)
    {
        ArgumentNullException.ThrowIfNull(exportIndices);

        var package = GetPackage(packageId);

        if ((package.Flags & 8) == 0) throw new InvalidDataException("该物理包需要真实 linker export 索引，不允许从 export 序号推断。");
        if (exportIndices.Count > Ue3PackageObjectRegistry.MaximumKnownObjects)
            throw new InvalidDataException("登记对象数量超限。");

        var registry = new Ue3PackageObjectRegistry(packageId, package.Flags, objectSlotCount, []);
        string root = System.IO.Path.GetFileNameWithoutExtension(package.Selection.PhysicalPackage);

        foreach (int index in exportIndices)
        {
            var item = GetExport(packageId, index);

            if (item.LogicalPath.Length < 2 || item.LogicalPath[0].Number != 0
                || !string.Equals(item.LogicalPath[0].Text, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("嵌入包的 outermost flags/capacity 尚未建立，不能沿用物理 linker root。");
            _ = ResolveObjectClass(packageId, index);
            if (item.SerializedNetIndex is not int netIndex)
                throw new InvalidDataException("此 export 尚无经校验的串行 NetIndex。");
            registry.TrackResolvedObject(item.ObjectId);
            registry.ApplyLoadedIndex(item.ObjectId, new Ue3NetIndexLoadContext(0, true, netIndex, package.Flags, -1));
        }
        return registry;
    }

    private bool HasDeclaredType(Ue3ExportIdentity value, (string Package, string Class, int PackageNumber, int ClassNumber) expected)
    {
        if (expected.PackageNumber != 0 || expected.ClassNumber != 0) return false;
        if (expected.Package.Equals("Core", StringComparison.OrdinalIgnoreCase)
            && expected.Class.Equals("Class", StringComparison.OrdinalIgnoreCase))
            return GetPackage(value.PackageId).Classes.ContainsKey(value.ExportIndex);
        if (value.ClassReference == 0) return false;

        var package = GetPackage(value.PackageId);
        var classPath = value.ClassReference > 0 ? GetExport(value.PackageId, value.ClassReference).LogicalPath
            : package.ImportPaths[-value.ClassReference - 1];

        return classPath.Length == 2 && classPath[0].Number == 0 && classPath[1].Number == 0
            && classPath[0].Text.Equals(expected.Package, StringComparison.OrdinalIgnoreCase)
            && classPath[1].Text.Equals(expected.Class, StringComparison.OrdinalIgnoreCase);
    }

    private PackageData GetPackage(long id) => packages.TryGetValue(id, out var value) ? value
        : throw new InvalidDataException("未显式选择的物理包身份。");

    internal static string PathKey(ImmutableArray<Ue3ObjectName> path) => string.Concat(path.Select(x =>
        x.Text.Length.ToString(CultureInfo.InvariantCulture) + ":" + x.Text.ToUpperInvariant() + ":"
        + x.Number.ToString(CultureInfo.InvariantCulture) + "/"));
    private static string Display(ImmutableArray<Ue3ObjectName> path) => string.Join('.', path.Select(x => x.Text + $"[{x.Number}]"));
}
