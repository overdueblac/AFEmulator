using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Objects;

/// <summary>
/// One explicit host-zombie mapping, a verified enum member, and class identity supported by bidirectional
/// CDO evidence.
/// </summary>
public sealed record Ue3BioHostClassBinding(Ue3BioHostClassEntry ExplicitValue,
    Ue3EnumMember EnumMember, Ue3ClassIdentity Class);

/// <summary>
/// Static class bindings for two independent arrays. Never dynamically loads classes, merges effective
/// defaults, infects, or equips.
/// </summary>
public sealed record Ue3BioHostClassBindings(Ue3ObjectSourceLocation DefaultSource,
    Ue3EnumDeclaration ZombieTypes, ImmutableArray<Ue3BioHostClassBinding> PawnClasses,
    ImmutableArray<Ue3BioHostClassBinding> WeaponClasses);

/// <summary>
/// Checks enum membership, unique physical classes, CDOs, and host-zombie bases against validated explicit
/// BIO defaults.
/// </summary>
public static class Ue3BioHostClassBindingReader
{
    /// <summary>
    /// Revalidates both complete serials before class binding. Rejects missing packages, ambiguous paths,
    /// and incompatible inheritance.
    /// </summary>
    /// <param name="defaultSerial">Complete TGBioMatch CDO serial.</param>
    /// <param name="enumSerial">Complete EZombieType export 400 serial.</param>
    /// <param name="expectedEnumSha256">Independent enum-serial trust anchor supplied by the caller.</param>
    /// <param name="identities">Explicit physical set including TGBioGameContent.u and required bases.</param>
    /// <param name="declarations">Array/structure declarations bound to the same identity catalog.</param>
    /// <param name="bioPackageId">Explicitly selected TGBioGame.u identity.</param>
    public static Ue3BioHostClassBindings Read(ReadOnlySpan<byte> defaultSerial, ReadOnlySpan<byte> enumSerial,
        string expectedEnumSha256, Ue3ObjectIdentityCatalog identities,
        Ue3PropertyDeclarationCatalog declarations, long bioPackageId)
    {
        var defaults = Ue3BioHostClassDefaultsReader.Read(defaultSerial, identities, declarations, bioPackageId);
        var enumeration = Ue3EnumDeclarationReader.Read(enumSerial, identities, bioPackageId, 400, expectedEnumSha256);

        if (enumeration.Identity.DisplayPath != "TGBioGame.TGBioType.EZombieType")
            throw new InvalidDataException("母体映射的枚举物理身份不符。");

        var pawns = Bind(defaults.PawnClasses, "TGBioGame.TGBioHostZombie", enumeration, identities);
        var weapons = Bind(defaults.WeaponClasses, "TGBioGame.TGBioWeap_HostZombieClaw", enumeration, identities);

        return new(defaults.Source, enumeration, pawns, weapons);
    }

    private static ImmutableArray<Ue3BioHostClassBinding> Bind(ImmutableArray<Ue3BioHostClassEntry> entries,
        string requiredBase, Ue3EnumDeclaration enumeration, Ue3ObjectIdentityCatalog identities)
    {
        var result = ImmutableArray.CreateBuilder<Ue3BioHostClassBinding>(entries.Length);
        HashSet<Ue3ObjectName> seen = [];

        foreach (var entry in entries)
        {
            var member = enumeration.Members.SingleOrDefault(x => x.Name == entry.ZombieType)
                ?? throw new InvalidDataException("母体映射的枚举 FName 不属于已恢复声明。");

            if (!seen.Add(member.Name)) throw new InvalidDataException("母体映射重复指定同一枚举成员。");

            // Profile class names are unnumbered FNames. Never parse display suffixes as numbers or synthesize missing packages.
            var path = entry.ClassName.Split('.');

            if (path.Length != 2 || path.Any(string.IsNullOrEmpty))
                throw new InvalidDataException("母体类名不符合已恢复的包名.类名 profile。");

            var definition = identities.ResolveUniqueLogicalPath(path.Select(x => new Ue3ObjectName(x, 0)).ToArray());
            var type = identities.GetClass(definition.PackageId, definition.ExportIndex);

            RequireBase(type, requiredBase, identities);
            result.Add(new(entry, member, type));
        }

        return result.MoveToImmutable();
    }

    private static void RequireBase(Ue3ClassIdentity type, string requiredBase, Ue3ObjectIdentityCatalog identities)
    {
        HashSet<long> visited = [];
        var current = type;

        while (visited.Count < 128 && visited.Add(current.Definition.ObjectId))
        {
            if (current.Definition.DisplayPath == requiredBase) return;

            var parent = identities.ResolveReference(current.Definition.PackageId, current.Definition.SuperReference);

            if (parent is null) break;
            current = identities.GetClass(parent.PackageId, parent.ExportIndex);
        }

        throw new InvalidDataException("母体类没有已验证的所需基类，或继承链循环/超过预算。");
    }
}
