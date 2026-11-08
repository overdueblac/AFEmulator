using System.Buffers.Binary;
using System.Text;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Read-only validation of ordinary-BIO PC local resources. Static field descriptions may cache; live
/// objects, arrays, and world are rechecked each time. Snapshots are not atomic. Callers still serialize
/// mutations through the exact instance's native-thread gate.
/// </summary>
sealed partial class BioResourceInspector(AfdevOwnedProcess process)
{
    private readonly Dictionary<(uint Type, string Path), ResourceProperty> properties = [];
    private readonly QueryInspector reader = new(process);
    private ResourceBinding? binding;
    public QueryInspector Reader => reader;

    /// <summary>
    /// Binds the factory-returned ordinary-BIO PC/PRI without Player and reads minimal spawn data/field
    /// evidence.
    /// </summary>
    public ResourceProfile Prepare(AfdevNativeEvidence world, uint controller)
    {
        if (binding != null)
            throw new InvalidOperationException("Resource inspector is already bound; do not rebind recycled objects.");

        QueryObject pc = reader.Identity(controller);
        uint priAddress = Object(controller, "Engine.Controller.PlayerReplicationInfo");
        QueryObject pri = reader.Identity(priAddress);

        if (reader.Path(pc.Class) != "TGBioGame.TGBioPlayerController" || reader.Path(pri.Class) != "TGBioGame.TGBioPlayerReplicationInfo")
            throw new InvalidDataException("Resources require the exact ordinary BIO factory pair.");
        binding = new ResourceBinding(world, pc, pri, reader.Identity(world.GameInfo));
        return Capture();
    }

    /// <summary>
    /// Reads minimal data only. A void native return or nonnull descriptor does not establish resource
    /// completion.
    /// </summary>
    public ResourceProfile Capture()
    {
        ResourceBinding current = CheckBinding();
        uint pri = current.Pri.Address;
        ResourceValue avatar = Struct(pri, "UTGame.TGPlayerReplicationInfo.CharInfo", "UTGame.TGAvatarChar_Data.AvatarCharInfo");
        ResourceValue charData = Struct(avatar, "UTGame.TGAvatarChar_Data.AvatarCharInfo.CharData", "UTGame.TGAvatarChar_Data.AvatarCharData");
        ResourceValue bagData = Struct(avatar, "UTGame.TGAvatarChar_Data.AvatarCharInfo.BagCharData", "UTGame.TGAvatarChar_Data.BagCharData");
        int family = Integer(avatar, "UTGame.TGAvatarChar_Data.AvatarCharInfo.nFamilyID");
        int avatarId = Integer(avatar, "UTGame.TGAvatarChar_Data.AvatarCharInfo.AvatarCharID");
        int bagIndex = Integer(bagData, "UTGame.TGAvatarChar_Data.BagCharData.DefaultBagIndex");
        ResourceArray equipped = Array(charData, "UTGame.TGAvatarChar_Data.AvatarCharData.ItemEquipItemIDs", 64);
        ResourceArray bags = Array(bagData, "UTGame.TGAvatarChar_Data.BagCharData.Bags", 16);
        ResourceArray sync = Array(new ResourceValue(pri, current.Pri.Class), "UTGame.TGPlayerReplicationInfo.SyncList", 256);
        ResourceString uin = String(new ResourceValue(pri, current.Pri.Class), "UTGame.TGPlayerReplicationInfo.Uin", 128);
        int hudCamp = Integer(new ResourceValue(pri, current.Pri.Class), "UTGame.TGPlayerReplicationInfo.HudCamp");
        int syncCamp = Integer(new ResourceValue(pri, current.Pri.Class), "UTGame.TGPlayerReplicationInfo.SyncCamp");
        uint team = Object(pri, "Engine.PlayerReplicationInfo.Team");
        uint skillManager = Object(current.Controller.Address, "TGBioGame.TGBioPlayerController.BioSkillMgr");

        CheckBinding();
        return new ResourceProfile(current.Controller, current.Pri, uin, hudCamp, syncCamp, team, avatar, family, avatarId, bagIndex, equipped, bags, sync, skillManager, properties.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Returns scalar-field addresses after strict reflection validation for native-thread command
    /// construction, without writing them.
    /// </summary>
    public uint ScalarAddress(uint instance, string path, string kind, uint size)
    {
        CheckBinding();

        QueryObject identity = reader.Identity(instance);
        ResourceProperty property = Property(identity.Class, path, kind, size);

        return checked(instance + property.Offset);
    }

    private ResourceBinding CheckBinding()
    {
        ResourceBinding current = binding ?? throw new InvalidOperationException("Call Prepare first.");

        if (reader.U32(0x02066BF8) != current.World.World || reader.Identity(current.Controller.Address) != current.Controller || reader.Identity(current.Pri.Address) != current.Pri || reader.Identity(current.World.GameInfo) != current.Game || reader.Path(current.Game.Class) != "TGBioGame.TGBioMatch" || Object(current.Controller.Address, "Engine.Actor.WorldInfo") != current.World.WorldInfo || Object(current.Pri.Address, "Engine.Actor.WorldInfo") != current.World.WorldInfo || Object(current.Controller.Address, "Engine.PlayerController.Player") != 0 || Object(current.Controller.Address, "Engine.Controller.PlayerReplicationInfo") != current.Pri.Address || Object(current.Pri.Address, "Engine.Actor.Owner") != current.Controller.Address || (reader.U32(current.Controller.Address + 0xB0) & 8) != 0 || (reader.U32(current.Pri.Address + 0xB0) & 8) != 0)
            throw new InvalidDataException("Resource binding, owner, world or deletion state changed.");
        return current;
    }

    private uint Object(uint instance, string path)
    {
        QueryObject identity = reader.Identity(instance);
        ResourceProperty property = Property(identity.Class, path, "ObjectProperty", 4);

        return reader.U32(checked(instance + property.Offset));
    }

    private int Integer(ResourceValue value, string path)
    {
        ResourceProperty property = Property(value.Type, path, "IntProperty", 4);

        return unchecked((int)reader.U32(checked(value.Address + property.Offset)));
    }

    private ResourceValue Struct(uint instance, string path, string typePath)
    {
        QueryObject identity = reader.Identity(instance);

        return Struct(new ResourceValue(instance, identity.Class), path, typePath);
    }

    private ResourceValue Struct(ResourceValue value, string path, string typePath)
    {
        ResourceProperty property = Property(value.Type, path, "StructProperty", null);
        uint type = reader.U32(property.Identity.Address + 0x84);

        reader.Identity(type);
        if (reader.Path(type) != typePath || reader.Path(reader.U32(type + 0x34)) != "Core.ScriptStruct" || reader.U32(type + 0x50) != property.Size)
            throw new InvalidDataException("Resource struct type/extent differs: " + path);
        return new ResourceValue(checked(value.Address + property.Offset), type);
    }

    private ResourceArray Array(ResourceValue value, string path, uint limit)
    {
        ResourceProperty property = Property(value.Type, path, "ArrayProperty", 12);
        uint address = checked(value.Address + property.Offset);
        byte[] bytes = process.ReadMemory(address, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));

        if (count > limit || capacity < count || capacity > limit * 4 || (capacity != 0 && data < 0x10000) || (data == 0 && (count != 0 || capacity != 0)))
            throw new InvalidDataException("Resource array descriptor exceeds reviewed bounds: " + path);
        return new ResourceArray(path, address, data, count, capacity, property.Identity.Address);
    }

    private ResourceString String(ResourceValue value, string path, uint limit)
    {
        ResourceProperty property = Property(value.Type, path, "StrProperty", 12);
        uint address = checked(value.Address + property.Offset);
        byte[] descriptor = process.ReadMemory(address, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(0, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(4, 4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8, 4));

        if (count > limit || capacity < count || capacity > limit * 4 || (capacity != 0 && data < 0x10000) || (data == 0 && (count != 0 || capacity != 0)))
            throw new InvalidDataException("Resource FString descriptor exceeds reviewed bounds: " + path);

        byte[] text = count == 0 ? [] : process.ReadMemory(data, checked((int)count * 2));

        if (text.Length != 0 && (text[^1] != 0 || text[^2] != 0))
            throw new InvalidDataException("Resource FString lacks its trailing terminator: " + path);
        return new ResourceString(address, data, count, capacity, text.Length == 0 ? "" : Encoding.Unicode.GetString(text.AsSpan(0, text.Length - 2)));
    }

    private ResourceProperty Property(uint type, string path, string kind, uint? size)
    {
        if (!properties.TryGetValue((type, path), out ResourceProperty? property))
        {
            string name = path[(path.LastIndexOf('.') + 1)..];
            uint field = reader.FindField(type, name, path);
            QueryObject identity = reader.Identity(field);
            string actualKind = reader.Path(identity.Class);
            uint dimension = reader.U32(field + 0x40);
            uint elementSize = reader.U32(field + 0x44);
            uint offset = reader.U32(field + 0x60);
            uint extent = reader.U32(type + 0x50);

            if (dimension != 1 || elementSize == 0 || elementSize > 65536 || offset > extent || elementSize > extent - offset)
                throw new InvalidDataException("Resource reflected property extent differs: " + path);
            property = new ResourceProperty(identity, path, actualKind, dimension, elementSize, offset, reader.U32(field + 0x48), reader.U32(field + 0x4C));
            properties.Add((type, path), property);
        }

        if (reader.Identity(property.Identity.Address) != property.Identity || property.Kind != "Core." + kind || (size.HasValue && property.Size != size.Value))
            throw new InvalidDataException("Resource property identity/kind/size differs: " + path);
        return property;
    }
}

sealed record ResourceBinding(AfdevNativeEvidence World, QueryObject Controller, QueryObject Pri, QueryObject Game);
sealed record ResourceProperty(QueryObject Identity, string Path, string Kind, uint Dimension, uint Size, uint Offset, uint Flags, uint HighFlags);
sealed record ResourceValue(uint Address, uint Type);
sealed record ResourceArray(string Path, uint Address, uint Data, uint Count, uint Capacity, uint Property);
sealed record ResourceString(uint Address, uint Data, uint Count, uint Capacity, string Value);
sealed record ResourceProfile(QueryObject Controller, QueryObject Pri, ResourceString Uin, int HudCamp, int SyncCamp, uint Team, ResourceValue CharInfo, int FamilyId, int AvatarId, int DefaultBagIndex, ResourceArray EquippedItems, ResourceArray Bags, ResourceArray SyncList, uint SkillManager, IReadOnlyList<ResourceProperty> Properties);
