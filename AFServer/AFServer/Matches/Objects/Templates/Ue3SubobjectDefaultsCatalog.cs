using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// Undecoded native tail of an ordinary template object. Zero length means serial ended immediately after
/// None.
/// </summary>
public sealed record Ue3SubobjectNativeTail(int LogicalOffset, int Bytes, string Sha256);

/// <summary>
/// Raw component-prefix fields/logical sources. SerializedNetIndex does not establish network registration.
/// </summary>
public sealed record Ue3SubobjectTemplatePrefix(int LogicalOffset, int Bytes, int TemplateOwnerClassReference,
    Ue3ObjectName TemplateName, int SerializedNetIndex);

/// <summary>
/// Explicit ordinary-component-template values backed by original bytes. Does not synthesize archetypes,
/// CDOs, INI, or native-constructor values. NetIndex is raw serial data only, granting no runtime object,
/// Actor channel, or network registration.
/// </summary>
public sealed record Ue3SubobjectDefaultEvidence(Ue3ExportIdentity Identity, Ue3ObjectSourceLocation Source,
    int TemplateOwnerClassReference, Ue3ObjectName TemplateName, int SerializedNetIndex,
    ImmutableArray<Ue3ExplicitDefaultEvidence> ExplicitDefaults, Ue3SubobjectNativeTail NativeTail)
{
    /// <summary>
    /// Complete 16-byte prefix for the supported profile, with source position matching serial start.
    /// </summary>
    public Ue3SubobjectTemplatePrefix TemplatePrefix => new(Source.LogicalOffset, 16,
        TemplateOwnerClassReference, TemplateName, SerializedNetIndex);
}

/// <summary>
/// Bounded ordinary-template evidence bound to the same trusted identities. Unextracted objects remain
/// explicitly missing.
/// </summary>
public sealed class Ue3SubobjectDefaultsCatalog
{
    private readonly Ue3ObjectIdentityCatalog identities;
    private readonly ImmutableDictionary<long, Ue3SubobjectDefaultEvidence> objects;

    internal Ue3SubobjectDefaultsCatalog(Ue3ObjectIdentityCatalog identities,
        IEnumerable<Ue3SubobjectDefaultEvidence> objects)
    {
        this.identities = identities;
        this.objects = objects.ToImmutableDictionary(item => item.Identity.ObjectId);
    }

    /// <summary>Ordinary templates with complete reread serials and retained tails.</summary>
    public int Count => objects.Count;

    /// <summary>
    /// Queries actual physical identity. Returns false for unextracted evidence, rather than treating
    /// missing data as empty tags.
    /// </summary>
    public bool TryGet(long packageId, int exportIndex, out Ue3SubobjectDefaultEvidence? evidence)
    {
        var identity = identities.GetExport(packageId, exportIndex);

        return objects.TryGetValue(identity.ObjectId, out evidence);
    }

    /// <summary>
    /// Retrieves validated ordinary templates. Rejects unextracted data so callers retain the known gap.
    /// </summary>
    public Ue3SubobjectDefaultEvidence Get(long packageId, int exportIndex)
        => TryGet(packageId, exportIndex, out var evidence) ? evidence!
            : throw new InvalidDataException("普通模板对象尚无显式值来源，不能按空默认值处理。");

    internal void RequireSameIdentities(Ue3ObjectIdentityCatalog catalog)
    {
        if (!ReferenceEquals(identities, catalog))
            throw new InvalidDataException("普通模板证据必须与建立时的可信身份目录共同使用。");
    }
}
