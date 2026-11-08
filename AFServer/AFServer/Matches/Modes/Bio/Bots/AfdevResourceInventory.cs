namespace AFServer.AssaultFire.Matches.Bots;
sealed partial class BioResourceInspector
{
    /// <summary>
    /// Checks native BIO resource caches. Expected keys/class names must come from actual Item mappings; a
    /// nonempty cache does not prove default-weapon readiness. Empty expectations support initial snapshots
    /// with Complete fixed to false.
    /// </summary>
    public ResourceInventoryResult VerifyInventoryClasses(IReadOnlyList<ResourceExpectedInventory> expected)
    {
        ResourceBinding current = CheckBinding();
        uint manager = Object(current.World.GameInfo, "UTGame.TGGame.MyRefInvManager");
        var entries = new List<ResourceInventoryEntry>();
        var keys = new HashSet<(int Type, int Id)>();

        if (expected.Any(item => item.WeaponSubType < 0 || item.WeaponSubId < 0 || string.IsNullOrEmpty(item.ClassPath)) || expected.Select(item => (item.WeaponSubType, item.WeaponSubId)).Distinct().Count() != expected.Count)
            throw new ArgumentException("Inventory expectations must contain unique verified resource keys/classes.");
        if (manager == 0)
            return new ResourceInventoryResult(false, null, null, entries, expected);

        QueryObject managerIdentity = CheckActor(manager, "TGBioGame.TGBioRefInvManager", current.World.GameInfo);
        ResourceArray array = Array(new ResourceValue(manager, managerIdentity.Class), "UTGame.TGRefInventoryManager.InventoryClassContentArray", 2048);
        uint entryType = StructElementType(array, "UTGame.TGRefInventoryManager.InventoryClassContent");
        uint stride = reader.U32(entryType + 0x50);
        ResourceProperty classProperty = Property(entryType, "UTGame.TGRefInventoryManager.InventoryClassContent.InventoryClass", "ClassProperty", 4);

        for (uint index = 0; index < array.Count; index++)
        {
            CheckBinding();

            ResourceValue value = new(checked(array.Data + stride * index), entryType);
            int type = Integer(value, "UTGame.TGRefInventoryManager.InventoryClassContent.WeaponSubType");
            int id = Integer(value, "UTGame.TGRefInventoryManager.InventoryClassContent.WeaponSubID");
            uint classAddress = reader.U32(checked(value.Address + classProperty.Offset));
            QueryObject? classIdentity = classAddress == 0 ? null : reader.Identity(classAddress);
            string? classPath = classIdentity == null ? null : reader.Path(classAddress);

            if (type < 0 || id < 0 || !keys.Add((type, id)))
                throw new InvalidDataException("Native inventory cache has invalid/duplicate resource keys.");
            if (classIdentity != null && (reader.Path(classIdentity.Class) != "Core.Class" || !IsClassDerivedFrom(classAddress, "Engine.Inventory")))
                throw new InvalidDataException("Native inventory cache contains a non-Inventory class.");
            entries.Add(new ResourceInventoryEntry(index, type, id, classIdentity, classPath));
        }

        ArrayInner(array, "StructProperty", stride);
        CheckBinding();
        if (reader.Identity(manager) != managerIdentity || Object(current.World.GameInfo, "UTGame.TGGame.MyRefInvManager") != manager)
            throw new InvalidDataException("Native inventory manager changed during snapshot.");

        bool complete = expected.Count > 0 && expected.All(item => entries.Any(entry => entry.WeaponSubType == item.WeaponSubType && entry.WeaponSubId == item.WeaponSubId && entry.ClassPath == item.ClassPath));

        return new ResourceInventoryResult(complete, managerIdentity, array, entries, expected);
    }

    private bool IsClassDerivedFrom(uint type, string expected)
    {
        var seen = new HashSet<uint>();

        while (type != 0)
        {
            if (seen.Count == 32 || !seen.Add(type))
                throw new InvalidDataException("Inventory class inheritance cycle/size differs.");
            reader.Identity(type);
            if (reader.Path(type) == expected)
                return true;
            type = reader.U32(type + 0x48);
        }

        return false;
    }
}

sealed record ResourceExpectedInventory(int ItemId, int WeaponSubType, int WeaponSubId, string ClassPath, string MappingEvidence);
sealed record ResourceInventoryEntry(uint Index, int WeaponSubType, int WeaponSubId, QueryObject? Class, string? ClassPath);
sealed record ResourceInventoryResult(bool Complete, QueryObject? Manager, ResourceArray? Array, IReadOnlyList<ResourceInventoryEntry> Entries, IReadOnlyList<ResourceExpectedInventory> Expected);
