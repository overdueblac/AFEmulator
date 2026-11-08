using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// An audit root and purpose explicitly selected by the caller, without claiming production mode or Uses
/// order.
/// </summary>
public sealed record Ue3DependencyRoot(string Purpose, long PackageId, int ExportIndex);

/// <summary>
/// Extracted direct physical-object dependency kinds, excluding unresolved script/native-constructor
/// dependencies.
/// </summary>
public enum Ue3DependencyKind
{
    /// <summary>Class reference from the export directory.</summary>
    Class,
    /// <summary>Super reference from the export directory.</summary>
    Super,
    /// <summary>Outer reference from the export directory.</summary>
    Outer,
    /// <summary>Archetype reference from the export directory.</summary>
    Archetype,
    /// <summary>CDO reference verified at the class-serial tail.</summary>
    ClassDefaultObject,
    /// <summary>Signed i32 reference in an explicit CDO ObjectProperty.</summary>
    ExplicitDefaultObject,
    /// <summary>Signed i32 reference in an ordinary component template's explicit ObjectProperty.</summary>
    ExplicitSubobjectObject,
    /// <summary>Template-owner reference read directly from the component prefix.</summary>
    ComponentTemplateOwner
}

/// <summary>
/// Source projection of an explicit tag, without merging inheritance, INI, archetype, or native-constructor
/// values.
/// </summary>
public sealed record Ue3ExplicitDefaultEvidence(Ue3ObjectName Name, int ArrayIndex, string SerializedType,
    int TagLogicalOffset, int PayloadLogicalOffset, int PayloadBytes, string PayloadSha256,
    JsonElement SerializedValue, JsonElement DeclarationBinding);

/// <summary>
/// Retains physical identity, raw flags/NetIndex, sources, and explicit defaults. Not an effective runtime
/// object.
/// </summary>
public sealed record Ue3DependencyNode(Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source,
    bool IsVerifiedClass, bool IsVerifiedDefaultObject, string? AncestryBlocker,
    ImmutableArray<Ue3ExplicitDefaultEvidence> ExplicitDefaults)
{
    /// <summary>
    /// Distinguishes extracted empty sets from unextracted ordinary objects, without claiming
    /// effective-default recovery.
    /// </summary>
    public string ExplicitDefaultsStatus { get; init; } = "not_extracted";

    /// <summary>
    /// Undecoded native tail of an ordinary component template. Null means no corresponding template-serial
    /// evidence exists.
    /// </summary>
    public Ue3SubobjectNativeTail? SubobjectNativeTail { get; init; }

    /// <summary>
    /// Recovered ordinary-component prefix, separate from original directory NetIndex and without changing
    /// registration state.
    /// </summary>
    public Ue3SubobjectTemplatePrefix? SubobjectTemplatePrefix { get; init; }
}

/// <summary>
/// One raw dependency, retaining zero, missing, and ambiguous references. TargetObjectId exists only after
/// unique resolution.
/// </summary>
public sealed record Ue3DependencyEdge(long FromObjectId, Ue3DependencyKind Kind, int PackageReference,
    Ue3ReferenceResolutionStatus Status, ImmutableArray<Ue3ObjectName> TargetPath, long? TargetObjectId,
    Ue3ObjectName? PropertyName, int? ArrayIndex, int? PayloadLogicalOffset);

/// <summary>
/// Read-only dependency audit results. Selection/traversal order is not production physical-package order
/// and grants no network index or creation rights.
/// </summary>
public sealed record Ue3ObjectDependencyReport(ImmutableArray<Ue3DependencyRoot> Roots,
    ImmutableArray<Ue3PhysicalPackageSelection> PhysicalSelection, ImmutableArray<Ue3DependencyNode> Nodes,
    ImmutableArray<Ue3DependencyEdge> Edges, ImmutableArray<string> UnrecoveredRuntimeRequirements);

/// <summary>
/// Builds a bounded actual dependency closure from trusted identities, retaining failed edges without
/// synthesizing missing objects. Covers directory, CDO, and optional ordinary-template top-level explicit
/// object references. Arrays, structures, scripts, and constructors remain to be recovered.
/// </summary>
public static class Ue3ObjectDependencyAudit
{
    /// <summary>
    /// Maximum distinct physical objects per audit. Exceeding the limit fails the entire audit rather than
    /// returning a truncated closure.
    /// </summary>
    public const int MaximumNodes = 8192;

    /// <summary>Maximum direct dependency edges per audit.</summary>
    public const int MaximumEdges = 65536;

    /// <summary>
    /// Traverses roots and raw tags in input order without changing catalogs, registration tables, or
    /// existing replication snapshots.
    /// </summary>
    /// <param name="catalog">
    /// Catalog loaded against external trust anchors with explicit physical-package selection.
    /// </param>
    /// <param name="roots">
    /// 1–64 distinct objects with stated purposes. Logical name matches cannot replace physical identity.
    /// </param>
    /// <param name="maximumNodes">Caller-tightened node budget, within the implementation limit.</param>
    /// <param name="maximumEdges">Caller-tightened edge budget, within the implementation limit.</param>
    /// <param name="subobjectDefaults">
    /// Optional ordinary-template evidence from the same catalog. Uncovered objects remain not_extracted.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Invalid roots, explicit tags, or source projections, or an over-budget closure.
    /// </exception>
    public static Ue3ObjectDependencyReport Capture(Ue3ObjectIdentityCatalog catalog,
        IReadOnlyList<Ue3DependencyRoot> roots, int maximumNodes = MaximumNodes, int maximumEdges = MaximumEdges,
        Ue3SubobjectDefaultsCatalog? subobjectDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(roots);
        subobjectDefaults?.RequireSameIdentities(catalog);
        Require(roots.Count is > 0 and <= 64 && maximumNodes is > 0 and <= MaximumNodes
            && maximumEdges is > 0 and <= MaximumEdges, "依赖审计根数量或预算无效。");

        var copiedRoots = roots.ToImmutableArray();
        var queue = new Queue<Ue3ExportIdentity>();
        HashSet<long> scheduled = [];
        HashSet<long> rootIds = [];
        var nodes = ImmutableArray.CreateBuilder<Ue3DependencyNode>();
        var edges = ImmutableArray.CreateBuilder<Ue3DependencyEdge>();

        void Schedule(Ue3ExportIdentity item)
        {
            if (!scheduled.Add(item.ObjectId)) return;
            Require(scheduled.Count <= maximumNodes, "依赖闭包超过节点预算；没有返回部分结果。");
            queue.Enqueue(item);
        }

        foreach (var root in copiedRoots)
        {
            Require(root is not null && !string.IsNullOrWhiteSpace(root.Purpose) && root.Purpose.Length <= 256,
                "依赖根必须有不超过 256 字符的用途说明。");

            var item = catalog.GetExport(root!.PackageId, root.ExportIndex);

            Require(rootIds.Add(item.ObjectId), "依赖根物理身份重复。");
            Schedule(item);
        }

        while (queue.TryDequeue(out var item))
        {
            bool isClass = catalog.TryGetClass(item.PackageId, item.ExportIndex, out var @class);
            bool isDefault = catalog.TryGetDefaultObjectClass(item.PackageId, item.ExportIndex, out var defaultClass);
            var source = catalog.GetExportSource(item.PackageId, item.ExportIndex);
            var facts = isDefault ? ReadExplicitDefaults(defaultClass!, source) : [];
            Ue3SubobjectDefaultEvidence? subobject = null;

            if (subobjectDefaults is not null && subobjectDefaults.TryGet(item.PackageId, item.ExportIndex, out subobject))
            {
                source = subobject!.Source;
                facts = subobject.ExplicitDefaults;
            }

            string? ancestryBlocker = null;

            if (isClass)
            {
                try { _ = catalog.ResolveClassAncestry(item.PackageId, item.ExportIndex); }
                catch (InvalidDataException error) { ancestryBlocker = error.Message; }
            }

            nodes.Add(new(item, source, isClass, isDefault, ancestryBlocker, facts)
            {
                ExplicitDefaultsStatus = isDefault ? "explicit_cdo_tags" : subobject is not null
                    ? "explicit_component_template_tags" : "not_extracted",
                SubobjectNativeTail = subobject?.NativeTail,
                SubobjectTemplatePrefix = subobject?.TemplatePrefix
            });

            void Add(Ue3DependencyKind kind, int reference, Ue3ExplicitDefaultEvidence? fact = null)
            {
                Require(edges.Count < maximumEdges, "依赖闭包超过边预算；没有返回部分结果。");

                var resolution = catalog.InspectReference(item.PackageId, reference);

                // The unresolved zero-class metaclass branch is a separate global blocker. Null here means only a zero raw-directory value.
                edges.Add(new(item.ObjectId, kind, reference, resolution.Status, resolution.LogicalPath,
                    resolution.Target?.ObjectId, fact?.Name, fact?.ArrayIndex, fact?.PayloadLogicalOffset));
                if (resolution.Target is not null) Schedule(resolution.Target);
            }

            Add(Ue3DependencyKind.Class, item.ClassReference);
            Add(Ue3DependencyKind.Super, item.SuperReference);
            Add(Ue3DependencyKind.Outer, item.OuterReference);
            Add(Ue3DependencyKind.Archetype, item.ArchetypeReference);
            if (isClass) Add(Ue3DependencyKind.ClassDefaultObject, @class!.DefaultObject.ExportIndex);
            if (subobject is not null) Add(Ue3DependencyKind.ComponentTemplateOwner, subobject.TemplateOwnerClassReference);
            foreach (var fact in facts)
            {
                if (fact.SerializedType == "ObjectProperty")
                    Add(subobject is null ? Ue3DependencyKind.ExplicitDefaultObject : Ue3DependencyKind.ExplicitSubobjectObject,
                        fact.SerializedValue.GetProperty("object_index").GetInt32(), fact);
            }
        }

        return new(copiedRoots, catalog.CaptureSelection(), nodes.ToImmutable(), edges.ToImmutable(),
        [
            "生产物理包顺序、Uses 附加字段和嵌入 Package flags/capacity 尚未确认。",
            "zero-class 元类、缺失 native 类型和对象构造尚未恢复。",
            "显式 CDO/普通模板 tag 不等于有效 defaults；继承、archetype、INI 和原生构造合成尚未完成。",
            "普通对象仅覆盖明确提供的组件模板；未提取对象、原生尾部、结构/数组、脚本和构造器中的依赖尚未覆盖。",
            "运行时类字段、Actor flags、拥有者/可见性及首次 open 的真实输入尚未建立。"
        ]);
    }

    private static ImmutableArray<Ue3ExplicitDefaultEvidence> ReadExplicitDefaults(Ue3ClassIdentity identity,
        Ue3ObjectSourceLocation source)
    {
        try
        {
            var facts = ImmutableArray.CreateBuilder<Ue3ExplicitDefaultEvidence>();
            HashSet<(string, int, int)> seen = [];
            int next = 4;

            foreach (var tag in identity.ExplicitDefaultTags)
            {
                Require(facts.Count < 16384, "单个 CDO 显式 tag 数量超限。");

                var name = Name(tag.GetProperty("name"));
                var type = Name(tag.GetProperty("type"));
                int arrayIndex = tag.GetProperty("array_index").GetInt32();

                Require(type.Number == 0 && arrayIndex >= 0 && seen.Add((name.Text.ToUpperInvariant(), name.Number, arrayIndex)),
                    "CDO 显式 tag 名称/数组槽重复或非法。");

                var extent = tag.GetProperty("source");
                int start = extent.GetProperty("tag_relative_offset").GetInt32();
                int payload = extent.GetProperty("payload_relative_offset").GetInt32();
                int bytes = extent.GetProperty("payload_bytes").GetInt32();
                string hash = extent.GetProperty("payload_sha256").GetString()!;
                int extra = type.Text switch { "BoolProperty" => 1, "ByteProperty" or "StructProperty" => 8, _ => 0 };

                Require(start == next && payload == checked(start + 24 + extra) && bytes >= 0
                    && (long)payload + bytes <= source.Bytes - 8 && hash is not null && hash.Length == 64
                    && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "CDO tag 来源范围不连续或越界。");
                next = checked(payload + bytes);

                var value = tag.GetProperty("serialized_value");
                var binding = tag.GetProperty("declaration_binding");

                if (type.Text == "ObjectProperty")
                {
                    Require(value.GetProperty("status").GetString() == "decoded"
                        && value.GetProperty("encoding").GetString() == "package_object_index_i32" && bytes == 4,
                        "显式对象引用没有完整 i32 来源。");

                    byte[] encoded = new byte[4];

                    BinaryPrimitives.WriteInt32LittleEndian(encoded, value.GetProperty("object_index").GetInt32());
                    Require(Convert.ToHexStringLower(SHA256.HashData(encoded)) == hash,
                        "显式对象引用 JSON 投影与原 payload SHA256 不一致。");
                }

                facts.Add(new(name, arrayIndex, type.Text, checked(source.LogicalOffset + start),
                    checked(source.LogicalOffset + payload), bytes, hash!, value.Clone(), binding.Clone()));
            }

            Require(next == source.Bytes - 8, "CDO tag 集合未覆盖完整 serial；不能静默遗漏依赖。");
            return facts.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or OverflowException or FormatException)
        {
            throw new InvalidDataException("CDO 依赖投影格式错误。", error);
        }
    }

    private static Ue3ObjectName Name(JsonElement value)
    {
        Require(value.GetArrayLength() == 3 && value[0].GetInt32() >= 0 && value[1].GetInt32() >= 0
            && !string.IsNullOrEmpty(value[2].GetString()), "CDO tag FName 非法。");
        return new(value[2].GetString()!, value[1].GetInt32());
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
