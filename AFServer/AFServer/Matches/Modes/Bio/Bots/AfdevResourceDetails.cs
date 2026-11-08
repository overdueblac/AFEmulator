using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
sealed partial class BioResourceInspector
{
    private static readonly int[] DefaultWeapons = [100013, 100099, 100009, 100010];
    private static readonly string[] DefaultSkills = ["TGBioGameContent.TGBioSkill_FastMove_Content", "TGBioGameContent.TGBioSkill_FlyCutter_Content", "TGBioGameContent.TGBioSkill_Trample_Content", "TGBioGameContent.TGBioSkill_Invisible_Content", "TGBioGameContent.TGBioSkill_Shield_Content"];
    /// <summary>
    /// Validates actual results of one default-profile initialization. The caller must supply Recover's
    /// synchronous return; snapshots cannot infer it. Completion covers character/default-backpack data
    /// only; inventory resources and spawned Pawns are checked separately.
    /// </summary>
    public ResourceAvatarResult VerifyDefaultAvatar(string expectedUin, int expectedCamp, bool recoverReturnedTrue)
    {
        if (expectedCamp is not (0 or 1) || string.IsNullOrEmpty(expectedUin))
            throw new ArgumentException("Explicit camp 0/1 and a nonempty internal Bot ID are required.");

        ResourceProfile profile = Capture();
        int[] equipped = IntElements(profile.EquippedItems, 64);
        uint bagType = StructElementType(profile.Bags, "UTGame.TGAvatarChar_Data.SingleBag");
        uint stride = reader.U32(bagType + 0x50);
        var bags = new List<ResourceBag>();
        int[] expectedEquipped = expectedCamp == 1 ? [100042, 100359, 100205, 100033, 100216, 300061, 300062, 300063, 300064, 300065, 100218, 100323, 100026, 100099] : [100042, 100359, 100205, 100033, 100217, 300066, 300067, 300068, 300069, 300070, 100219, 100324, 100026, 100099];

        for (uint index = 0; index < profile.Bags.Count; index++)
        {
            CheckBinding();

            ResourceValue bag = new(checked(profile.Bags.Data + index * stride), bagType);
            ResourceArray items = Array(bag, "UTGame.TGAvatarChar_Data.SingleBag.DataItemIDs", 32);
            ResourceArray skills = Array(bag, "UTGame.TGAvatarChar_Data.SingleBag.SkillEquipItemIDs", 32);

            bags.Add(new ResourceBag(index, items, IntElements(items, 32), skills, IntElements(skills, 32)));
        }

        var checks = new Dictionary<string, bool>
        {
            ["RecoverSyncArrayInfoReturnedTrue"] = recoverReturnedTrue,
            ["InternalUinMatches"] = profile.Uin.Value == expectedUin,
            ["HudCampMatches"] = profile.HudCamp == expectedCamp,
            ["SyncCampMatches"] = profile.SyncCamp == expectedCamp,
            ["DefaultFamilyMatches"] = profile.FamilyId == (expectedCamp == 1 ? 12 : 13),
            ["AvatarIdMatches"] = profile.AvatarId == 0,
            ["DefaultBagIndexMatches"] = profile.DefaultBagIndex == 0,
            ["EquippedItemsMatch"] = equipped.SequenceEqual(expectedEquipped),
            ["FiveBagsPresent"] = bags.Count == 5,
            ["DefaultWeaponItemsMatch"] = bags.Count == 5 && bags[0].Items.SequenceEqual(DefaultWeapons),
            ["RemainingBagsEmpty"] = bags.Skip(1).All(item => item.Items.Length == 0),
            ["DefaultBagSkillsEmpty"] = bags.All(item => item.Skills.Length == 0)
        };

        CheckBinding();
        return new ResourceAvatarResult(checks.Values.All(value => value), checks, profile, equipped, expectedEquipped, DefaultWeapons, bags, "process-event-static.json: array_property_constructor / array_property_serialize_item");
    }

    /// <summary>
    /// Validates one instance of each of five BIO skills, including Actor owner, OwnerPC, and manager
    /// ownership.
    /// </summary>
    public ResourceSkillResult VerifySkills()
    {
        ResourceBinding current = CheckBinding();
        uint controller = current.Controller.Address;
        uint manager = Object(controller, "TGBioGame.TGBioPlayerController.BioSkillMgr");
        var skills = new List<ResourceSkill>();
        var seen = new HashSet<uint>();

        if (manager == 0)
            return new ResourceSkillResult(false, null, [], DefaultSkills);

        QueryObject managerIdentity = CheckActor(manager, "TGBioGame.TGBioSkillManager", controller);

        if (Object(manager, "TGBioGame.TGBioSkillManager.OwnerPC") != controller)
            throw new InvalidDataException("BIO skill manager OwnerPC differs.");

        uint item = Object(manager, "TGBioGame.TGBioSkillManager.SkillChain");

        while (item != 0)
        {
            CheckBinding();
            if (seen.Count == 16 || !seen.Add(item))
                throw new InvalidDataException("BIO skill chain cycle/size differs.");

            QueryObject identity = CheckActor(item, null, controller);
            string type = reader.Path(identity.Class);
            uint ownerPc = Object(item, "TGBioGame.TGBioSkillBase.OwnerPC");
            uint ownerManager = Object(item, "TGBioGame.TGBioSkillBase.OwnerSkillManager");
            uint next = Object(item, "TGBioGame.TGBioSkillBase.Next");

            if (!reader.IsA(item, "TGBioGame.TGBioSkillBase") || ownerPc != controller || ownerManager != manager)
                throw new InvalidDataException("BIO skill type/owner binding differs.");
            skills.Add(new ResourceSkill(identity, type, ownerPc, ownerManager, next));
            item = next;
        }

        CheckBinding();
        if (reader.Identity(manager) != managerIdentity || Object(controller, "TGBioGame.TGBioPlayerController.BioSkillMgr") != manager)
            throw new InvalidDataException("BIO skill manager changed during snapshot.");

        bool ready = skills.Count == DefaultSkills.Length && skills.Select(skill => skill.Class).Order(StringComparer.Ordinal).SequenceEqual(DefaultSkills.Order(StringComparer.Ordinal));

        return new ResourceSkillResult(ready, managerIdentity, skills, DefaultSkills);
    }

    private QueryObject CheckActor(uint actor, string? expectedClass, uint expectedOwner)
    {
        ResourceBinding current = CheckBinding();
        QueryObject identity = reader.Identity(actor);

        if ((expectedClass != null && reader.Path(identity.Class) != expectedClass) || Object(actor, "Engine.Actor.WorldInfo") != current.World.WorldInfo || Object(actor, "Engine.Actor.Owner") != expectedOwner || (reader.U32(actor + 0xB0) & 8) != 0)
            throw new InvalidDataException("Resource actor class/owner/world/deletion state differs.");
        return identity;
    }

    private QueryObject ArrayInner(ResourceArray array, string kind, uint? size)
    {
        CheckBinding();

        QueryObject property = reader.Identity(array.Property);
        uint inner = reader.U32(array.Property + 0x84);
        QueryObject identity = reader.Identity(inner);
        uint elementSize = reader.U32(inner + 0x44);

        if (reader.Path(property.Class) != "Core.ArrayProperty" || identity.Outer != array.Property || reader.Path(inner) != array.Path + "." + array.Path[(array.Path.LastIndexOf('.') + 1)..] || reader.Path(identity.Class) != "Core." + kind || reader.U32(inner + 0x40) != 1 || reader.U32(inner + 0x60) != 0 || elementSize == 0 || elementSize > 65536 || (size.HasValue && elementSize != size.Value))
            throw new InvalidDataException("Resource array inner property type/extent differs: " + array.Path);

        byte[] descriptor = process.ReadMemory(array.Address, 12);

        if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0, 4)) != array.Data || BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(4, 4)) != array.Count || BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8, 4)) != array.Capacity)
            throw new InvalidDataException("Resource array changed during snapshot: " + array.Path);
        return identity;
    }

    private int[] IntElements(ResourceArray array, uint limit)
    {
        ArrayInner(array, "IntProperty", 4);
        if (array.Count > limit)
            throw new InvalidDataException("Resource integer array exceeds its bound.");

        byte[] bytes = array.Count == 0 ? [] : process.ReadMemory(array.Data, checked((int)array.Count * 4));
        int[] values = Enumerable.Range(0, (int)array.Count).Select(index => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 4, 4))).ToArray();

        ArrayInner(array, "IntProperty", 4);
        return values;
    }

    private uint StructElementType(ResourceArray array, string expectedType)
    {
        QueryObject inner = ArrayInner(array, "StructProperty", null);
        uint type = reader.U32(inner.Address + 0x84);

        reader.Identity(type);
        if (reader.Path(type) != expectedType || reader.Path(reader.U32(type + 0x34)) != "Core.ScriptStruct" || reader.U32(type + 0x50) != reader.U32(inner.Address + 0x44))
            throw new InvalidDataException("Resource struct array element differs: " + array.Path);
        return type;
    }
}

sealed record ResourceBag(uint Index, ResourceArray ItemArray, int[] Items, ResourceArray SkillArray, int[] Skills);
sealed record ResourceAvatarResult(bool Complete, IReadOnlyDictionary<string, bool> Checks, ResourceProfile Profile, int[] Equipped, int[] ExpectedEquipped, int[] ExpectedWeapons, IReadOnlyList<ResourceBag> Bags, string ArrayLayoutEvidence);
sealed record ResourceSkill(QueryObject Identity, string Class, uint OwnerPc, uint OwnerManager, uint Next);
sealed record ResourceSkillResult(bool Complete, QueryObject? Manager, IReadOnlyList<ResourceSkill> Skills, string[] ExpectedClasses);
