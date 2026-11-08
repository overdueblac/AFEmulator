using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Declaration reread from complete property serial. Type references remain signed indices in the owning
/// package.
/// </summary>
public sealed record Ue3PropertyDeclaration(Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source,
    string Kind, int NextFieldReference, int ArrayDimension, ulong PropertyFlags,
    ImmutableDictionary<string, int> TypeReferences);

/// <summary>
/// Original structure declaration/flags sources. Defaults retain hashes only, without synthesized member
/// defaults.
/// </summary>
public sealed record Ue3ScriptStructDeclaration(Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source,
    int SuperReference, int ChildrenReference, uint StructFlags, int FlagsLogicalOffset,
    int DefaultsLogicalOffset, int DefaultsBytes, string DefaultsSha256);

/// <summary>
/// Bounded declaration evidence bound to trusted physical identity. Lookup checks actual owners across the
/// full catalog before requiring extraction, so missing derived-class declarations cannot silently become
/// same-named ancestor fields.
/// </summary>
public sealed class Ue3PropertyDeclarationCatalog
{
    private readonly Ue3ObjectIdentityCatalog identities;
    private readonly ImmutableDictionary<long, Ue3PropertyDeclaration> properties;
    private readonly ImmutableDictionary<long, Ue3ScriptStructDeclaration> structs;
    private readonly Dictionary<(long Owner, string Name, int Number), List<Ue3ExportIdentity>> candidates = [];

    internal Ue3PropertyDeclarationCatalog(Ue3ObjectIdentityCatalog identities,
        IEnumerable<Ue3PropertyDeclaration> properties, IEnumerable<Ue3ScriptStructDeclaration> structs)
    {
        this.identities = identities;
        this.properties = properties.ToImmutableDictionary(x => x.Identity.ObjectId);
        this.structs = structs.ToImmutableDictionary(x => x.Identity.ObjectId);

        foreach (var package in identities.CaptureSelection())
        foreach (var item in identities.CaptureExports(package.PackageId))
        {
            if (item.OuterReference <= 0 || item.ClassReference == 0) continue;

            var type = identities.InspectReference(item.PackageId, item.ClassReference).LogicalPath;

            if (type.Length != 2 || type[0] != new Ue3ObjectName("Core", 0) || type[1].Number != 0
                || !type[1].Text.EndsWith("Property", StringComparison.Ordinal)) continue;

            var name = item.LogicalPath[^1];
            var key = (identities.GetExport(item.PackageId, item.OuterReference).ObjectId, name.Text.ToUpperInvariant(), name.Number);

            if (!candidates.TryGetValue(key, out var matches)) candidates.Add(key, matches = []);
            matches.Add(item);
        }
    }

    /// <summary>Properties whose complete serials were consumed exactly.</summary>
    public int PropertyCount => properties.Count;

    /// <summary>Structures with complete serial sources and located flags.</summary>
    public int StructCount => structs.Count;

    /// <summary>
    /// Retrieves the specified physical-property declaration, explicitly rejecting unextracted data.
    /// </summary>
    public Ue3PropertyDeclaration GetProperty(long packageId, int exportIndex)
        => properties.TryGetValue(identities.GetExport(packageId, exportIndex).ObjectId, out var value)
            ? value : throw new InvalidDataException("属性声明尚未提取，不能推断其类型或 ArrayDim。");

    /// <summary>
    /// Retrieves the specified physical-structure declaration, explicitly rejecting unextracted data.
    /// </summary>
    public Ue3ScriptStructDeclaration GetStruct(long packageId, int exportIndex)
        => structs.TryGetValue(identities.GetExport(packageId, exportIndex).ObjectId, out var value)
            ? value : throw new InvalidDataException("ScriptStruct 声明尚未提取，不能推断其 serializer。");

    /// <summary>
    /// Finds actual fields through verified inheritance, preserving FName number and rejecting duplicates,
    /// missing data, or unextracted declarations.
    /// </summary>
    public Ue3PropertyDeclaration BindClassProperty(long classPackageId, int classExportIndex, Ue3ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (var owner in identities.ResolveClassAncestry(classPackageId, classExportIndex))
        {
            var found = FindOwnedProperty(owner.Definition, name);

            if (found is not null) return found;
        }

        throw new InvalidDataException("tag 没有实际类继承中的字段声明，不能按字段名称猜测。");
    }

    /// <summary>
    /// Reads every field in original Children/Next order, rejecting missing data, cross-owner references,
    /// cycles, and extra declarations.
    /// </summary>
    public ImmutableArray<Ue3PropertyDeclaration> GetStructFields(Ue3ScriptStructDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!structs.TryGetValue(declaration.Identity.ObjectId, out var trusted) || trusted != declaration)
            throw new InvalidDataException("结构声明不属于本证据目录。");
        if (declaration.SuperReference != 0)
            throw new InvalidDataException("当前结构字段 profile 尚不支持 super 合成。");

        var result = ImmutableArray.CreateBuilder<Ue3PropertyDeclaration>();
        HashSet<long> seen = [];
        int reference = declaration.ChildrenReference;

        while (reference != 0)
        {
            if (reference < 0 || result.Count >= 256)
                throw new InvalidDataException("结构字段链存在外部引用或超过预算。");

            var field = GetProperty(declaration.Identity.PackageId, reference);

            if (field.Identity.OuterReference != declaration.Identity.ExportIndex || !seen.Add(field.Identity.ObjectId))
                throw new InvalidDataException("结构字段链跨 owner 或循环。");
            if (FindOwnedProperty(declaration.Identity, field.Identity.LogicalPath[^1]) != field)
                throw new InvalidDataException("结构字段链与 owner 目录不一致。");

            result.Add(field);
            reference = field.NextFieldReference;
        }

        int owned = candidates.Where(x => x.Key.Owner == declaration.Identity.ObjectId).Sum(x => x.Value.Count);

        if (owned != result.Count) throw new InvalidDataException("结构 Children 链没有覆盖全部实际属性声明。");
        return result.ToImmutable();
    }

    internal Ue3ScriptStructDeclaration ResolveStruct(Ue3PropertyDeclaration field)
    {
        if (field.Kind != "StructProperty" || !field.TypeReferences.TryGetValue("struct", out int reference) || reference == 0)
            throw new InvalidDataException("字段没有明确 StructProperty 类型引用。");

        var target = identities.ResolveReference(field.Identity.PackageId, reference)!;

        return GetStruct(target.PackageId, target.ExportIndex);
    }

    internal void RequireSameIdentities(Ue3ObjectIdentityCatalog catalog)
    {
        if (!ReferenceEquals(identities, catalog))
            throw new InvalidDataException("声明证据必须与建立时的可信身份目录共同使用。");
    }

    private Ue3PropertyDeclaration? FindOwnedProperty(Ue3ExportIdentity owner, Ue3ObjectName name)
    {
        if (!candidates.TryGetValue((owner.ObjectId, name.Text.ToUpperInvariant(), name.Number), out var matches)) return null;
        if (matches.Count != 1) throw new InvalidDataException("同一 owner/FName 对应多个属性，不能静默选择。");
        return GetProperty(matches[0].PackageId, matches[0].ExportIndex);
    }
}
