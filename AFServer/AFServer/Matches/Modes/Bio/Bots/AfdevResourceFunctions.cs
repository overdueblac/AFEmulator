using System.Security.Cryptography;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Read-only allowlist for later resource calls. Reflected descriptor validation grants neither call
/// permission nor proof of loaded resources.
/// </summary>
sealed class ResourceFunctionCatalog(AfdevOwnedProcess process)
{
    private readonly QueryInspector reader = new(process);
    // Current TGBioMatch.Login calls ChangeTeam(PC, IntZero, False); this is not inferred from enum names.
    public const int HumanCamp = 0;
    public const string HumanCampEvidence = "TGBioGame.TGBioMatch.Login: memory 181..202, script SHA256 a9699055747b0d7318d572c2d7ebbaed8dffe535ea2d11b765a7b35064c7c750";
    // Current TGBioMatch.PreLogin passes 3 and 15 to both GetInventoryClass and DynamicLoadInventoryClass.
    public const int BioThrowWeaponSubType = 3;
    public const int BioThrowWeaponSubId = 15;
    public const string BioThrowEvidence = "TGBioGame.TGBioMatch.PreLogin: memory 532..546 / 569..583, script SHA256 d207a92b7049a4b4d815c2f7a37f3cdd3a7c27e9bbd04eed7fa1640edbabe6ca";
    /// <summary>
    /// Describes exact BIO PostLoadCharacter. Call once per new PC; repeated calls create duplicate skills.
    /// </summary>
    public ResourceFunction DescribePostLoadCharacter(uint controller) => Describe(controller, "TGBioGame.TGBioPlayerController", "TGBioGame.TGBioPlayerController.PostLoadCharacter", 0x20002, 0, 0, 0xFFFF, 536, "cb65a0d2b50b763a9165910b036570f6c7acec1d5322d00f11c597bcf2935e4f", []);
    /// <summary>
    /// Read-only use of the existing loaded predicate. Native code returns true when there is neither
    /// Player nor matching standby connection.
    /// </summary>
    public ResourceFunction DescribeHasClientLoadedCurrentWorld(uint controller)
    {
        ResourceFunction function = Describe(controller, "TGBioGame.TGBioPlayerController", "Engine.PlayerController.HasClientLoadedCurrentWorld", 0x20401, 4, 4, 0, 2, "ccaf083429bf5a89fa542d244998f2bbd2f8837281164524d82f612695ccb1db", [new("ReturnValue", "BoolProperty", 4, 0, 0x580)]);

        if (function.ExecPointer != 0x00952280)
            throw new InvalidDataException("HasClientLoadedCurrentWorld native0 exec differs from current PE proof.");
        return function;
    }

    /// <summary>
    /// Reuses server-local completion assignment only after validating all resources for this bot.
    /// </summary>
    public ResourceFunction DescribeSetProcessingComplete(uint controller) => Describe(controller, "TGBioGame.TGBioPlayerController", "UTGame.TGPlayerController.ServerSetProcessingComplete", 0x2200C2, 4, 0, 0xFFFF, 134, "8f2d3d5182d2e0941dfa99970837c82e4ff72208dd8edb89a3d1862c0bc6d6c1", [new("PRI", "ObjectProperty", 4, 0, 0, "UTGame.TGPlayerReplicationInfo")]);
    /// <summary>
    /// Explicitly selects TGPC base-class local completion semantics. Verify that no loading-check timer
    /// exists first so the call only clears waiting without automatic RestartPlayer. Bypasses TGRoundPC's
    /// human late-join/count-adjustment override.
    /// </summary>
    public ResourceFunction DescribeBaseNotifyLoadingComplete(uint controller) => Describe(controller, "TGBioGame.TGBioPlayerController", "UTGame.TGPlayerController.NotifyClientLoadingProcessComplete", 0x2200C2, 0, 0, 0xFFFF, 539, "672e3ffc75ced385be82de205b9c10bfcd50848270f6e4f0df4f9f79f560547f", [], declaringClassPath: "UTGame.TGPlayerController");
    public ResourceFunction DescribeCanRestartPlayer(uint controller) => Describe(controller, "TGBioGame.TGBioPlayerController", "UTGame.TGPlayerController.CanRestartPlayer", 0x20002, 8, 4, 0, 139, "9f92f0bc9ab3c9da764209e52a1a9653284a2bb63183851b33c1b5507d0130dc", [new("ReturnValue", "BoolProperty", 4, 0, 0x580), new("Game", "ObjectProperty", 4, 4, 0, "UTGame.UTGame")]);
    /// <summary>
    /// Starts legally from current state. PendingMatch uses the engine state entry; after arbitration,
    /// Global.StartMatch dispatches to TGBioMatch. Never bypasses state functions. Starting invokes the
    /// spawn entry once automatically through StartHumans.
    /// </summary>
    public ResourceFunction DescribeStartMatch(uint game)
    {
        QueryObject receiver = reader.Identity(game);
        uint frame = reader.U32(game + 0x18);
        uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);

        if (reader.Path(receiver.Class) != "TGBioGame.TGBioMatch")
            throw new InvalidDataException("StartMatch requires this exact ordinary BIO GameInfo.");
        if (state != 0)
        {
            reader.Identity(state);
            try
            {
                reader.FindField(state, "StartMatch", "Engine.GameInfo.PendingMatch.StartMatch");
                return Describe(game, "TGBioGame.TGBioMatch", "Engine.GameInfo.PendingMatch.StartMatch", 0x20002, 0, 0, 0xFFFF, 39, "36623930e6f08c4462f3f0e0167b732d1f11ef53c662a7ceb86eef07f558bf07", [], explicitSearchType: state);
            }
            catch (MissingFieldException)
            {
            // Use the ordinary BIO class-level entry only when current state has no StartMatch override.
            }
        }

        return Describe(game, "TGBioGame.TGBioMatch", "TGBioGame.TGBioMatch.StartMatch", 0x20002, 12, 0, 0xFFFF, 527, "0acb264973d92abc00aaea6f80031fd47670885c67337dcf3da57621f442ee4c", [new("PRI", "ObjectProperty", 4, 0, 0, "TGBioGame.TGBioPlayerReplicationInfo"), new("Idx", "IntProperty", 4, 4, 0), new("totalPlayerCount", "IntProperty", 4, 8, 0)]);
    }

    /// <summary>
    /// Legal top-level ordinary-BIO spawn entry, with local structure size reflected from the exact
    /// ScriptStruct type.
    /// </summary>
    public ResourceFunction DescribeRestartPlayer(uint game)
    {
        const string path = "TGBioGame.TGBioMatch.RestartPlayer";
        const string changeType = "TGBioGame.TGBioType.ZombieChangeNecessity";
        const string baseType = "TGBioGame.TGBioType.ZombieBaseInfo";
        QueryObject receiver = reader.Identity(game);
        uint function = reader.FindField(receiver.Class, "RestartPlayer", path);
        uint changeSize = LocalStructSize(function, path, "ZombieChange", changeType);
        uint baseSize = LocalStructSize(function, path, "BaseInfo", baseType);
        uint baseOffset = checked(8 + changeSize);
        uint propertiesSize = checked(baseOffset + baseSize);

        return Describe(game, "TGBioGame.TGBioMatch", path, 0x820002, propertiesSize, 4, 0xFFFF, 1204, "720745f0abff8b51840a7afa5e4563c1a8e28fb105ea104f58b2a912a5eb986c", [new("NewPlayer", "ObjectProperty", 4, 0, 0x80, "Engine.Controller"), new("PRI", "ObjectProperty", 4, 4, 0, "TGBioGame.TGBioPlayerReplicationInfo"), new("ZombieChange", "StructProperty", changeSize, 8, 0, changeType), new("BaseInfo", "StructProperty", baseSize, baseOffset, 0, baseType)]);
    }

    /// <summary>
    /// Describes class-level ChangeTeam. Callers must separately resolve any current GameInfo state
    /// override; this descriptor is not virtual-dispatch resolution.
    /// </summary>
    public ResourceFunction DescribeChangeTeam(uint game) => Describe(game, "TGBioGame.TGBioMatch", "UTGame.TGTeamGame.ChangeTeam", 0x20002, 24, 16, 12, 1234, "6f2f70ee36229e9e1e1c285401066a3076237d19a00c1891d475e8b970482fa5", [new("Other", "ObjectProperty", 4, 0, 0x80, "Engine.Controller"), new("Num", "IntProperty", 4, 4, 0x80), new("bNewTeam", "BoolProperty", 4, 8, 0x80), new("ReturnValue", "BoolProperty", 4, 12, 0x580), new("NewTeam", "ObjectProperty", 4, 16, 0, "UTGame.UTTeamInfo"), new("PendingTeam", "ObjectProperty", 4, 20, 0, "UTGame.UTTeamInfo")]);
    /// <summary>
    /// Calls only an existing BIO resource manager with four default backpack Item IDs. Empty arrays must
    /// not trigger an all-weapons fallback.
    /// </summary>
    public ResourceFunction DescribeLoadDefaultInventory(uint manager)
    {
        const string path = "UTGame.TGRefInventoryManager.DynamicLoadAllInventoryClasses";
        uint function = reader.FindField(reader.Identity(manager).Class, "DynamicLoadAllInventoryClasses", path);
        uint scriptCount = reader.U32(function + 0x58);

        // The two ported weapons extend only default-branch entries 214/215. Four-item backpack branches and argument layouts stay unchanged.
        // Retain exact lengths and source-storage fingerprints per revision. Unknown revisions still fail; evidence is in docs/weapon-port/.
        string storageHash = scriptCount switch
        {
            20250 => "7dc37980b9e02ff4ca8ba8f7b7899d492f0ac1c89598682bc0784bf9d0997b83",
            20328 => "edf30bea61a279abb2cde8ff5a267a62852b68a41efbe42207716b5e0ebfacdc",
            20406 => "b13acee1ee0cf267ad56fc13b3b15055935d1f13c8b9bf6ab96f885dfaf396d6",
            _ => throw new InvalidDataException("BIO 默认武器加载脚本不是已登记的原版或武器移植修订。")
        };

        return Describe(manager, "TGBioGame.TGBioRefInvManager", path, 0x20002, 20, 12, 0xFFFF, scriptCount, storageHash,
            [new("ItemIDArray", "ArrayProperty", 12, 0, 0x400080, "Core.IntProperty"),
             new("Index", "IntProperty", 4, 12, 0),
             new("InventoryResData", "ObjectProperty", 4, 16, 0, "UTGame.TGResDataWeapon")]);
    }
    /// <summary>
    /// When MyRefInvManager is empty, creates the BIO resource manager with native Actor.Spawn. The caller
    /// fixes SpawnClass to TGBioRefInvManager and SpawnOwner to current GameInfo, validates returned
    /// ownership, then binds MyRefInvManager.
    /// </summary>
    public ResourceFunction DescribeSpawnInventoryManager(uint game)
    {
        ResourceFunction function = Describe(game, "TGBioGame.TGBioMatch", "Engine.Actor.Spawn", 0x24401, 52, 52, 48, 71, "119cc4a1fe82999ff4511005b25706e7e58bb4c623275666b2db05224c5a6dc8", [new("SpawnClass", "ClassProperty", 4, 0, 0x80, "Core.Class"), new("SpawnOwner", "ObjectProperty", 4, 4, 0x90, "Engine.Actor"), new("SpawnTag", "NameProperty", 8, 8, 0x90), new("SpawnLocation", "StructProperty", 12, 16, 0x90, "Core.Object.Vector"), new("SpawnRotation", "StructProperty", 12, 28, 0x90, "Core.Object.Rotator"), new("ActorTemplate", "ObjectProperty", 4, 40, 0x90, "Engine.Actor"), new("bNoCollisionFail", "BoolProperty", 4, 44, 0x90), new("ReturnValue", "ObjectProperty", 4, 48, 0xD80, "Engine.Actor")]);

        if (function.ExecPointer != 0x00BE9120)
            throw new InvalidDataException("Actor.Spawn native0 exec differs from its current PE registration.");
        return function;
    }

    /// <summary>
    /// Loads verified resource keys. BIO-specific throwable keys are
    /// BioThrowWeaponSubType/BioThrowWeaponSubId.
    /// </summary>
    public ResourceFunction DescribeLoadInventoryClass(uint manager) => Describe(manager, "TGBioGame.TGBioRefInvManager", "UTGame.TGRefInventoryManager.DynamicLoadInventoryClass", 0x820002, 24, 8, 0xFFFF, 640, "89112801d231c166496c705b9ce07a1831fe54c4f09a4a8d34b48f601dd5349c", [new("WeaponSubType", "IntProperty", 4, 0, 0x80), new("WeaponSubID", "IntProperty", 4, 4, 0x80), new("ICC", "StructProperty", 12, 8, 0, "UTGame.TGRefInventoryManager.InventoryClassContent"), new("Index", "IntProperty", 4, 20, 0)]);
    public ResourceFunction DescribeGetInventoryClass(uint manager) => Describe(manager, "TGBioGame.TGBioRefInvManager", "UTGame.TGRefInventoryManager.GetInventoryClass", 0x20002, 16, 12, 8, 377, "175e417f2230bf0879458d896b7397d06451ce48dd5ea00f93c8ab83e3c55420", [new("WeaponSubType", "IntProperty", 4, 0, 0x80), new("WeaponSubID", "IntProperty", 4, 4, 0x80), new("ReturnValue", "ClassProperty", 4, 8, 0x580, "Core.Class"), new("Index", "IntProperty", 4, 12, 0)]);
    private uint LocalStructSize(uint function, string path, string name, string expectedType)
    {
        uint property = reader.FindField(function, name, path + "." + name);
        QueryObject identity = reader.Identity(property);
        uint type = reader.U32(property + 0x84);
        QueryObject typeIdentity = reader.Identity(type);
        uint size = reader.U32(type + 0x50);

        if (reader.Path(identity.Class) != "Core.StructProperty" || reader.Path(type) != expectedType || reader.Path(typeIdentity.Class) != "Core.ScriptStruct" || reader.U32(property + 0x44) != size || size == 0 || size > 16384 || (size & 3) != 0)
            throw new InvalidDataException("BIO RestartPlayer local struct type/size differs: " + name);
        return size;
    }

    internal ResourceFunction Describe(uint receiver, string receiverClass, string path, uint flags, uint propertiesSize, ushort parmsSize, ushort returnOffset, uint scriptCount, string storageHash, ResourceParameterExpectation[] expected, string? declaringClassPath = null, uint? explicitSearchType = null)
    {
        QueryObject identity = reader.Identity(receiver);
        uint searchClass = explicitSearchType ?? identity.Class;
        var seenClasses = new HashSet<uint>();

        while (declaringClassPath != null && reader.Path(searchClass) != declaringClassPath)
        {
            if (seenClasses.Count == 32 || !seenClasses.Add(searchClass))
                throw new InvalidDataException("Explicit resource function declaring class is not in receiver ancestry.");
            searchClass = reader.U32(searchClass + 0x48);
            reader.Identity(searchClass);
        }

        uint function = reader.FindField(searchClass, path[(path.LastIndexOf('.') + 1)..], path);
        QueryObject functionIdentity = reader.Identity(function);
        uint script = reader.U32(function + 0x54);
        uint capacity = reader.U32(function + 0x5C);
        uint exec = reader.U32(function + 0xC8);
        var parameters = new List<ResourceParameter>();

        if (reader.Path(identity.Class) != receiverClass || reader.Path(functionIdentity.Class) != "Core.Function" || reader.U32(function + 0xAC) != flags || reader.U16(function + 0xB0) != 0 || reader.U32(function + 0x50) != propertiesSize || reader.U16(function + 0xBE) != parmsSize || reader.U16(function + 0xC0) != returnOffset || reader.U32(function + 0x58) != scriptCount || capacity < scriptCount || capacity > scriptCount + 4096 || script < 0x10000 || exec < 0x00400000 || exec >= 0x02000000 || reader.U32(reader.U32(receiver) + 0x120) != 0x0075A9F0)
            throw new InvalidDataException("Resource function metadata differs: " + path);

        uint child = reader.U32(function + 0x4C);

        foreach (ResourceParameterExpectation item in expected)
        {
            QueryObject property = reader.Identity(child);
            uint typeOrMask = item.Kind is "ObjectProperty" or "ClassProperty" or "StructProperty" or "BoolProperty" or "ArrayProperty" ? reader.U32(child + 0x84) : 0;

            if (property.Outer != function || reader.Path(child) != path + "." + item.Name || reader.Path(property.Class) != "Core." + item.Kind || reader.U32(child + 0x40) != 1 || reader.U32(child + 0x44) != item.Size || reader.U32(child + 0x60) != item.Offset || reader.U32(child + 0x48) != item.Flags || reader.U32(child + 0x4C) != 0 || item.Offset > propertiesSize || item.Size > propertiesSize - item.Offset || (item.TargetType != null && item.Kind != "ArrayProperty" && reader.Path(typeOrMask) != item.TargetType) || (item.Kind == "BoolProperty" && (typeOrMask == 0 || (typeOrMask & (typeOrMask - 1)) != 0)))
                throw new InvalidDataException("Resource function parameter differs: " + path + "." + item.Name);
            if (item.Kind == "ArrayProperty")
            {
                QueryObject inner = reader.Identity(typeOrMask);

                if (inner.Outer != child || reader.Path(inner.Class) != item.TargetType || reader.Path(inner.Address) != path + "." + item.Name + "." + item.Name || reader.U32(inner.Address + 0x40) != 1 || reader.U32(inner.Address + 0x44) != 4 || reader.U32(inner.Address + 0x60) != 0)
                    throw new InvalidDataException("Resource call array element metadata differs: " + path);
            }

            parameters.Add(new ResourceParameter(property, item.Name, item.Kind, item.Offset, item.Size, item.Flags, typeOrMask, item.TargetType));
            child = reader.U32(child + 0x3C);
        }

        if (child != 0)
            throw new InvalidDataException("Unexpected resource function child: " + path);

        byte[] bytes = process.ReadMemory(script, checked((int)scriptCount));

        return new ResourceFunction(identity, functionIdentity, path, flags, 0, propertiesSize, parmsSize, returnOffset, exec, script, scriptCount, capacity, Convert.ToHexString(SHA256.HashData(bytes)), storageHash, parameters);
    }
}

sealed record ResourceParameterExpectation(string Name, string Kind, uint Size, uint Offset, uint Flags, string? TargetType = null);
sealed record ResourceParameter(QueryObject Identity, string Name, string Kind, uint Offset, uint Size, uint Flags, uint TypeOrBoolMask, string? TargetType);
sealed record ResourceFunction(QueryObject Receiver, QueryObject Function, string Path, uint Flags, ushort NativeIndex, uint PropertiesSize, ushort ParmsSize, ushort ReturnOffset, uint ExecPointer, uint ScriptPointer, uint ScriptCount, uint ScriptCapacity, string RuntimeScriptSha256, string PackageStorageScriptSha256, IReadOnlyList<ResourceParameter> Parameters);
