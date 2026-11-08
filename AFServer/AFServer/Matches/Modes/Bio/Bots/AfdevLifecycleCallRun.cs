using System.Buffers.Binary;
using System.Diagnostics;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
enum LifecycleOperation : uint
{
    HasClientLoadedCurrentWorld = 1,
    ChangeTeam = 2,
    PostLoadCharacter = 3,
    RestartPlayer = 4,
    SpawnInventoryManager = 5,
    SetProcessingComplete = 6,
    BaseNotifyLoadingComplete = 7,
    CanRestartPlayer = 8,
    LoadDefaultInventory = 9,
    LoadBioThrowInventory = 10,
    StartMatch = 11,
    AllocatePlayerIdentity = 12,
    SetPlayerName = 13
}

/// <summary>
/// Fixed method allowlist, one call per process. Unknown completion, gate failure, or abnormal return never
/// retries.
/// </summary>
static partial class LifecycleCallRun
{
    public static async Task<LifecycleCallResult> ExecuteAsync(AfdevOwnedProcess process, AfdevNativeEvidence evidence, uint controller, uint commandAddress, LifecycleOperation operation, Action<string, object> save, Action<string> report, CancellationToken cancellationToken, string botUin, string? nickname = null, uint preparationCommand = 0)
    {
        if (preparationCommand != 0)
            AfdevBioBotPreparationInspector.RequirePreparing(process, evidence, preparationCommand);

        var reader = new QueryInspector(process);
        var catalog = new ResourceFunctionCatalog(process);
        var identityCatalog = new AfdevBioBotIdentityFunctions(process);
        uint manager = operation is LifecycleOperation.LoadDefaultInventory or LifecycleOperation.LoadBioThrowInventory ? reader.U32(reader.FieldAddress(evidence.GameInfo, "MyRefInvManager", "UTGame.TGGame.MyRefInvManager", "ObjectProperty", 4)) : 0;
        uint pri = reader.U32(reader.FieldAddress(controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));

        if (reader.Path(reader.Identity(controller).Class) != "TGBioGame.TGBioPlayerController" || reader.Path(reader.Identity(pri).Class) != "TGBioGame.TGBioPlayerReplicationInfo" || reader.U32(0x02066BF8) != evidence.World || reader.U32(evidence.GameInfo + 0x34) != evidence.GameInfoClass)
            throw new InvalidDataException("Lifecycle requires the exact factory BIO PC/PRI/world identities.");

        ResourceFunction method = operation switch
        {
            LifecycleOperation.HasClientLoadedCurrentWorld => catalog.DescribeHasClientLoadedCurrentWorld(controller),
            LifecycleOperation.ChangeTeam => catalog.DescribeChangeTeam(evidence.GameInfo),
            LifecycleOperation.PostLoadCharacter => catalog.DescribePostLoadCharacter(controller),
            LifecycleOperation.RestartPlayer => catalog.DescribeRestartPlayer(evidence.GameInfo),
            LifecycleOperation.SpawnInventoryManager => catalog.DescribeSpawnInventoryManager(evidence.GameInfo),
            LifecycleOperation.SetProcessingComplete => catalog.DescribeSetProcessingComplete(controller),
            LifecycleOperation.BaseNotifyLoadingComplete => catalog.DescribeBaseNotifyLoadingComplete(controller),
            LifecycleOperation.CanRestartPlayer => catalog.DescribeCanRestartPlayer(controller),
            LifecycleOperation.LoadDefaultInventory => catalog.DescribeLoadDefaultInventory(manager),
            LifecycleOperation.LoadBioThrowInventory => catalog.DescribeLoadInventoryClass(manager),
            LifecycleOperation.StartMatch => catalog.DescribeStartMatch(evidence.GameInfo),
            LifecycleOperation.AllocatePlayerIdentity => identityCatalog.DescribeNextPlayerId(evidence.GameInfo),
            LifecycleOperation.SetPlayerName => identityCatalog.DescribeSetPlayerName(pri),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))};
        uint expectedExec = operation switch
        {
            LifecycleOperation.HasClientLoadedCurrentWorld => 0x00952280,
            LifecycleOperation.SpawnInventoryManager => 0x00BE9120,
            LifecycleOperation.AllocatePlayerIdentity => AfdevBioBotIdentityFunctions.NextPlayerIdExec,
            _ => 0x0048DB10
        };

        if (method.ExecPointer != expectedExec || method.ParmsSize > 52 || (method.ParmsSize & 3) != 0)
            throw new InvalidDataException("Lifecycle exact execution or parameter layout differs.");

        uint ordinalBefore = reader.U32(commandAddress + LifecycleCallHook.CompletedOrdinal);
        uint operationMask = 1u << ((int)operation - 1);

        if (ordinalBefore >= 13 || (reader.U32(commandAddress + LifecycleCallHook.CompletedOperations) & operationMask) != 0 || (ordinalBefore > 0 && (reader.U32(commandAddress + 8) != 1 || reader.U32(commandAddress + 12) != 1)))
            throw new InvalidDataException("Lifecycle method is already completed or previous command completion is unconfirmed; no retry.");

        uint ordinal = ordinalBefore + 1;
        uint frame = reader.U32(method.Receiver.Address + 0x18);
        uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);

        if (state != 0 && operation != LifecycleOperation.BaseNotifyLoadingComplete)
        {
            reader.Identity(state);
            try
            {
                uint resolved = reader.FindField(state, method.Path[(method.Path.LastIndexOf('.') + 1)..], method.Path);

                if (resolved != method.Function.Address)
                    throw new InvalidDataException("Lifecycle state-level function resolves elsewhere.");
            }
            catch (MissingFieldException)
            {
            }
        }

        var roots = new List<uint>
        {
            evidence.GameInfo,
            controller,
            pri,
            method.Receiver.Address,
            method.Function.Address,
            state
        };
        var proofs = new List<LifecycleProof>();

        void Prove(uint address, uint expected, uint mask = uint.MaxValue)
        {
            if ((reader.U32(address) & mask) != expected)
                throw new InvalidDataException("Lifecycle metadata proof changed during preparation.");
            proofs.Add(new(address, expected, mask));
        }

        uint Field(uint instance, string path, string kind = "ObjectProperty", uint size = 4)
        {
            string name = path[(path.LastIndexOf('.') + 1)..];
            uint property = reader.FindField(reader.U32(instance + 0x34), name, path);
            uint address = reader.FieldAddress(instance, name, path, kind, size);

            roots.Add(property);
            Prove(property + 0x40, 1);
            Prove(property + 0x44, size);
            Prove(property + 0x60, address - instance);
            if (kind is "ObjectProperty" or "ClassProperty")
            {
                uint type = reader.U32(property + 0x84);

                roots.Add(type);
                Prove(property + 0x84, type);
            }

            if (kind == "BoolProperty")
                Prove(property + 0x84, reader.U32(property + 0x84));
            return address;
        }

        uint pcPri = Field(controller, "Engine.Controller.PlayerReplicationInfo");
        uint priOwner = Field(pri, "Engine.Actor.Owner");
        uint pcWorld = Field(controller, "Engine.Actor.WorldInfo");
        uint priWorld = Field(pri, "Engine.Actor.WorldInfo");
        uint pcPlayer = Field(controller, "Engine.PlayerController.Player");

        if (reader.U32(pcPri) != pri || reader.U32(priOwner) != controller || reader.U32(pcWorld) != evidence.WorldInfo || reader.U32(priWorld) != evidence.WorldInfo || reader.U32(pcPlayer) != 0)
            throw new InvalidDataException("Lifecycle exact pair ownership or unconnected Player gate differs.");

        uint fn = method.Function.Address;

        Prove(fn + 0x50, method.PropertiesSize);
        Prove(fn + 0x54, method.ScriptPointer);
        Prove(fn + 0x58, method.ScriptCount);
        Prove(fn + 0x5C, method.ScriptCapacity);
        Prove(fn + 0xAC, method.Flags);
        Prove(fn + 0xB0, method.NativeIndex, 0xFFFF);
        Prove(fn + 0xBE, method.ParmsSize, 0xFFFF);
        Prove(fn + 0xC0, method.ReturnOffset, 0xFFFF);
        Prove(fn + 0xC8, method.ExecPointer);
        Prove(fn + 0x4C, method.Parameters.FirstOrDefault()?.Identity.Address ?? 0);
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            ResourceParameter parameter = method.Parameters[i];
            uint pointer = parameter.Identity.Address;

            roots.Add(pointer);
            Prove(pointer + 0x3C, i + 1 < method.Parameters.Count ? method.Parameters[i + 1].Identity.Address : 0);
            Prove(pointer + 0x40, 1);
            Prove(pointer + 0x44, parameter.Size);
            Prove(pointer + 0x48, parameter.Flags);
            Prove(pointer + 0x4C, 0);
            Prove(pointer + 0x60, parameter.Offset);
            if (parameter.Kind is "ObjectProperty" or "ClassProperty" or "StructProperty" or "BoolProperty")
            {
                Prove(pointer + 0x84, parameter.TypeOrBoolMask);
                if (parameter.Kind != "BoolProperty")
                    roots.Add(parameter.TypeOrBoolMask);
                else if (parameter.TypeOrBoolMask != 1)
                    throw new InvalidDataException("Lifecycle bool parameter mask is not the reviewed mask 1.");
            }

            if (parameter.Kind == "StructProperty")
                Prove(parameter.TypeOrBoolMask + 0x50, parameter.Size);
            if (parameter.Kind == "ArrayProperty")
            {
                uint inner = parameter.TypeOrBoolMask;

                roots.Add(inner);
                Prove(pointer + 0x84, inner);
                Prove(inner + 0x40, 1);
                Prove(inner + 0x44, 4);
                Prove(inner + 0x60, 0);
            }
        }

        if (operation == LifecycleOperation.BaseNotifyLoadingComplete)
        {
            var loadingInspector = new BioResourceInspector(process);

            loadingInspector.Prepare(evidence, controller);

            ResourceLoadingState loading = loadingInspector.CaptureLoadingState();

            save("lifecycle-loading-before-base-notify", loading);
            if (!loading.LoadingTimerAbsent)
                throw new InvalidDataException("Loading timer exists; base notification could restart automatically.");

            uint timerAddress = Field(controller, "Engine.Actor.Timers", "ArrayProperty", 12);

            if (timerAddress != loading.TimerArray.Address)
                throw new InvalidDataException("Loading timer property changed.");
            Prove(timerAddress, loading.TimerArray.Data);
            Prove(timerAddress + 4, loading.TimerArray.Count);
            Prove(timerAddress + 8, loading.TimerArray.Capacity);

            uint inner = reader.U32(loading.TimerArray.Property + 0x84);
            uint timerType = reader.U32(inner + 0x84);
            uint name = reader.FindField(timerType, "FuncName", "Engine.Actor.TimerData.FuncName");
            uint nameOffset = reader.U32(name + 0x60);

            roots.Add(inner);
            roots.Add(timerType);
            roots.Add(name);
            Prove(loading.TimerArray.Property + 0x84, inner);
            Prove(inner + 0x84, timerType);
            Prove(name + 0x60, nameOffset);
            Prove(name + 0x44, 8);
            Prove(name + 0x40, 1);
            foreach (ResourceTimer timer in loading.Timers)
            {
                Prove(timer.Address + nameOffset, timer.NameIndex);
                Prove(timer.Address + nameOffset + 4, timer.NameNumber);
            }
        }

        uint managerField = 0, managerClass = 0, managerOwnerOffset = 0, managerWorldOffset = 0;
        uint inventoryPolicyOffset = 0, inventoryPolicyMask = 0;
        uint playerIdAddress = 0, botBitAddress = 0, botBitMask = 0, previousPlayerId = 0, previousSequence = 0;
        uint numPlayersAddress = 0, numBotsAddress = 0;
        byte[] parameters = new byte[Math.Max((int)method.ParmsSize, 4)];
        byte[]? borrowedArray = null;
        byte[]? borrowedName = null;

        void Parameter(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan((int)offset, 4), value);
        if (operation is LifecycleOperation.ChangeTeam or LifecycleOperation.RestartPlayer)
            Parameter(0, controller);
        if (operation is LifecycleOperation.HasClientLoadedCurrentWorld or LifecycleOperation.ChangeTeam or LifecycleOperation.CanRestartPlayer)
            Parameter(method.ReturnOffset, uint.MaxValue);
        if (operation == LifecycleOperation.AllocatePlayerIdentity)
        {
            playerIdAddress = Field(pri, "Engine.PlayerReplicationInfo.PlayerID", "IntProperty");
            botBitAddress = Field(pri, "Engine.PlayerReplicationInfo.bBot", "BoolProperty");

            uint botProperty = reader.FindField(reader.U32(pri + 0x34), "bBot", "Engine.PlayerReplicationInfo.bBot");

            botBitMask = reader.U32(botProperty + 0x84);
            previousPlayerId = reader.U32(playerIdAddress);
            previousSequence = reader.U32(0x01FA8E50);
            numPlayersAddress = Field(evidence.GameInfo, "Engine.GameInfo.NumPlayers", "IntProperty");
            numBotsAddress = Field(evidence.GameInfo, "Engine.GameInfo.NumBots", "IntProperty");
            if (botBitMask == 0 || (botBitMask & (botBitMask - 1)) != 0 || previousSequence >= int.MaxValue ||
                (preparationCommand == 0 && (reader.U32(numPlayersAddress) != 0 || reader.U32(numBotsAddress) != 0)))
                throw new InvalidDataException("Bot 身份分配要求本局准备边界、有效位域及未溢出的原生序号。");
            Parameter(method.ReturnOffset, uint.MaxValue);
        }

        if (operation == LifecycleOperation.SetPlayerName)
        {
            if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 63 || nickname.Any(char.IsControl))
                throw new ArgumentException("Bot 昵称必须是最多 63 个 UTF-16 字符的可显示名称。", nameof(nickname));
            borrowedName = System.Text.Encoding.Unicode.GetBytes(nickname + "\0");
            Parameter(0, commandAddress + 0xE00);
            Parameter(4, (uint)nickname.Length + 1);
            Parameter(8, (uint)nickname.Length + 1);
        }

        if (operation == LifecycleOperation.ChangeTeam)
            Parameter(4, ResourceFunctionCatalog.HumanCamp);
        if (operation is LifecycleOperation.SpawnInventoryManager or LifecycleOperation.LoadDefaultInventory or LifecycleOperation.LoadBioThrowInventory)
        {
            managerField = Field(evidence.GameInfo, "UTGame.TGGame.MyRefInvManager");
            if (operation == LifecycleOperation.SpawnInventoryManager && preparationCommand == 0 && reader.U32(managerField) != 0)
                throw new InvalidDataException("MyRefInvManager is already bound; it must not be replaced.");
            managerClass = manager == 0 ? FindClass(process, reader, "TGBioGame.TGBioRefInvManager", reader.Identity(evidence.GameInfoClass).Class) : reader.Identity(manager).Class;
            if (reader.Path(managerClass) != "TGBioGame.TGBioRefInvManager")
                throw new InvalidDataException("Exact BIO resource manager class differs.");
            roots.Add(managerClass);

            uint ownerProperty = reader.FindField(managerClass, "Owner", "Engine.Actor.Owner");
            uint worldProperty = reader.FindField(managerClass, "WorldInfo", "Engine.Actor.WorldInfo");

            // PRI/PC Field proofs cover the same Actor-property metadata; additionally validate manager-class property ranges.
            managerOwnerOffset = reader.U32(ownerProperty + 0x60);
            managerWorldOffset = reader.U32(worldProperty + 0x60);
            if (managerOwnerOffset + 4 > reader.U32(managerClass + 0x50) || managerWorldOffset + 4 > reader.U32(managerClass + 0x50))
                throw new InvalidDataException("Spawned manager inherited Actor property bounds differ.");
            if (operation == LifecycleOperation.SpawnInventoryManager)
            {
                Parameter(0, managerClass);
                Parameter(4, evidence.GameInfo);
            }

            if (operation == LifecycleOperation.LoadBioThrowInventory)
            {
                Parameter(0, ResourceFunctionCatalog.BioThrowWeaponSubType);
                Parameter(4, ResourceFunctionCatalog.BioThrowWeaponSubId);
            }

            if (operation == LifecycleOperation.LoadDefaultInventory)
            {
                const string policyPath = "UTGame.TGRefInventoryManager.bFindAnyWeapon";
                uint policyAddress = Field(manager, policyPath, "BoolProperty");
                uint policyProperty = reader.FindField(managerClass, "bFindAnyWeapon", policyPath);

                inventoryPolicyOffset = policyAddress - manager;
                inventoryPolicyMask = reader.U32(policyProperty + 0x84);
                if (inventoryPolicyMask == 0 || (inventoryPolicyMask & (inventoryPolicyMask - 1)) != 0)
                    throw new InvalidDataException("共享武器查找策略不是已核实的单一布尔位。");

                var inspector = new BioResourceInspector(process);

                inspector.Prepare(evidence, controller);

                ResourceAvatarResult avatar = inspector.VerifyDefaultAvatar(botUin, 0, true);

                if (!avatar.Complete || avatar.ExpectedWeapons.Length != 4)
                    throw new InvalidDataException("Exact four default weapon items are not ready.");
                borrowedArray = new byte[16];
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(borrowedArray.AsSpan(i * 4, 4), avatar.ExpectedWeapons[i]);
                Parameter(0, commandAddress + 0xE00);
                Parameter(4, 4);
                Parameter(8, 4);
            }
        }

        IReadOnlyList<QueryObject> identities = IdentityClosure(reader, roots);

        // Merge identical proofs for inherited properties referenced by different instances. Never silently overwrite conflicting proofs.
        proofs = proofs.Distinct().ToList();
        if (proofs.Count is 0 or > 104)
            throw new InvalidDataException("Lifecycle metadata proof closure exceeds fixed command bounds.");

        uint parms = commandAddress + 0xD00, expectedParms = commandAddress + 0xD80;
        byte[] command = new byte[LifecycleCallHook.CommandSize];

        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(offset, 4), value);
        Set(0x40, process.MainThreadId);
        Set(0x44, reader.U32(0x02063820));
        Set(0x48, evidence.World);
        Set(0x4C, evidence.WorldInfo);
        Set(0x50, evidence.GameInfo);
        Set(0x54, evidence.GameInfoClass);
        Set(0x58, fn);
        Set(0x5C, parms);
        Set(0x60, method.Receiver.Address);
        Set(0x64, method.Receiver.Class);
        Set(0x70, controller);
        Set(0x74, pri);
        Set(0x7C, (uint)operation);
        Set(0x80, ordinal);
        Set(0x84, (uint)proofs.Count);
        Set(0x88, (uint)identities.Count);
        Set(0x8C, method.ReturnOffset);
        Set(0x98, method.ParmsSize);
        Set(0x9C, expectedParms);
        Set(0xA0, frame);
        Set(0xA4, state);
        Set(0xB0, operationMask);
        Set(0xC0, pcPri);
        Set(0xC4, priOwner);
        Set(0xC8, pcWorld);
        Set(0xCC, priWorld);
        Set(0xD0, pcPlayer);
        Set(0xD4, managerField);
        Set(0xD8, managerClass);
        Set(0xDC, managerOwnerOffset);
        Set(0xE0, managerWorldOffset);
        Set((int)LifecycleCallHook.PreparationPage, preparationCommand);
        if (operation == LifecycleOperation.LoadDefaultInventory)
        {
            Set((int)LifecycleCallHook.InventoryPolicyOffset, inventoryPolicyOffset);
            Set((int)LifecycleCallHook.InventoryPolicyMask, inventoryPolicyMask);
        }

        if (operation == LifecycleOperation.AllocatePlayerIdentity)
        {
            Set(0x90, numPlayersAddress);
            Set(0x94, numBotsAddress);
            Set(0xD4, playerIdAddress);
            Set(0xD8, botBitAddress);
            Set(0xDC, botBitMask);
            Set(0xE0, 0x01FA8E50);
            Set(0xE8, previousSequence);
            Set(0xEC, previousPlayerId);
        }

        if (borrowedName != null)
            Set(0xF4, (uint)borrowedName.Length / 2);

        int cursor = 0x100;

        foreach (QueryObject identity in identities)
            foreach (uint value in new[]
            {
                identity.Address,
                identity.Index,
                identity.Outer,
                identity.NameIndex,
                identity.NameNumber,
                identity.Class
            }

            )
            {
                Set(cursor, value);
                cursor += 4;
            }

        cursor = 0x800;
        foreach (LifecycleProof proof in proofs)
            foreach (uint value in new[]
            {
                proof.Address,
                proof.Expected,
                proof.Mask
            }

            )
            {
                Set(cursor, value);
                cursor += 4;
            }

        save("lifecycle-prepared-" + operation, new ExecuteAsyncJson1 { method = method, operation = operation, ordinal = ordinal, identities = identities, proofs = proofs, frame = frame, state = state, managerField = managerField, managerClass = managerClass, managerOwnerOffset = managerOwnerOffset, managerWorldOffset = managerWorldOffset, CommandHex = Convert.ToHexString(command), ParametersHex = Convert.ToHexString(parameters), ScriptHex = Convert.ToHexString(process.ReadMemory(method.ScriptPointer, (int)method.ScriptCount)) });

        CommandIdleAckReceipt idle = await ResourceCallRun.DisarmAndWaitAsync(process, commandAddress, cancellationToken);

        save("lifecycle-idle-ack-" + operation, idle);
        process.WriteMemory(commandAddress + 4, command.AsSpan(4).ToArray());
        process.WriteMemory(parms, parameters);
        process.WriteMemory(expectedParms, parameters);
        if (borrowedArray != null)
        {
            process.WriteMemory(commandAddress + 0xE00, borrowedArray);
            process.WriteMemory(commandAddress + 0xE40, borrowedArray);
        }

        if (borrowedName != null)
        {
            process.WriteMemory(commandAddress + 0xE00, borrowedName);
            process.WriteMemory(commandAddress + 0xE80, borrowedName);
        }

        if (!process.ReadMemory(commandAddress, command.Length).AsSpan().SequenceEqual(command) || !process.ReadMemory(parms, parameters.Length).AsSpan().SequenceEqual(parameters) || !process.ReadMemory(expectedParms, parameters.Length).AsSpan().SequenceEqual(parameters))
            throw new InvalidDataException("Lifecycle unarmed payload readback differs.");
        if (borrowedArray != null && (!process.ReadMemory(commandAddress + 0xE00, 16).AsSpan().SequenceEqual(borrowedArray) || !process.ReadMemory(commandAddress + 0xE40, 16).AsSpan().SequenceEqual(borrowedArray)))
            throw new InvalidDataException("Lifecycle borrowed item array readback differs.");
        if (borrowedName != null &&
            (!process.ReadMemory(commandAddress + 0xE00, borrowedName.Length).AsSpan().SequenceEqual(borrowedName) ||
             !process.ReadMemory(commandAddress + 0xE80, borrowedName.Length).AsSpan().SequenceEqual(borrowedName)))
            throw new InvalidDataException("Lifecycle borrowed nickname readback differs.");
        report("LIFECYCLE_ARMED " + operation + " ordinal=" + ordinal);

        var observations = new List<QueryObservation>();
        uint resultAddress = method.ReturnOffset == 0xFFFF ? parms : parms + method.ReturnOffset;

        try
        {
            process.WriteMemory(commandAddress, [1, 0, 0, 0]);

            var timer = Stopwatch.StartNew();

            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.IsAlive)
                    throw new IOException("Lifecycle process exited; unknown completion is not retried.");

                QueryObservation sample = QueryRun.Read(process, commandAddress, resultAddress);

                observations.Add(sample);
                if (sample.Complete == 1)
                {
                    if (sample.Requested != 1 || sample.Started != 1 || sample.Status != 1 || sample.LastGuard != 0 || sample.ThreadBefore != process.MainThreadId || sample.ThreadAfter != process.MainThreadId || sample.StackBefore == 0 || sample.StackBefore != sample.StackAfter || sample.WorldBefore != evidence.World || sample.WorldAfter != evidence.World || sample.GameBefore != evidence.GameInfo || sample.GameAfter != evidence.GameInfo || sample.ClassBefore != evidence.GameInfoClass || sample.ClassAfter != evidence.GameInfoClass || reader.U32(commandAddress + LifecycleCallHook.CompletedOrdinal) != ordinal || (reader.U32(commandAddress + LifecycleCallHook.CompletedOperations) & operationMask) == 0)
                        throw new InvalidDataException("Lifecycle return lost exact thread/world/identity/stack/ordinal evidence.");
                    if (operation is LifecycleOperation.HasClientLoadedCurrentWorld or LifecycleOperation.ChangeTeam or LifecycleOperation.CanRestartPlayer)
                        if (sample.Result is not (0 or 1) || sample.ParmsValue != sample.Result)
                            throw new InvalidDataException("Lifecycle bool return is not canonical.");
                    if (borrowedArray != null && (!process.ReadMemory(commandAddress + 0xE00, 16).AsSpan().SequenceEqual(borrowedArray) || !process.ReadMemory(parms, parameters.Length).AsSpan().SequenceEqual(parameters)))
                        throw new InvalidDataException("Lifecycle inventory method changed its borrowed item array; allocation remains until process exit.");
                    if (manager != 0 && (reader.U32(managerField) != manager || reader.Identity(manager) != method.Receiver || reader.U32(manager + managerOwnerOffset) != evidence.GameInfo || reader.U32(manager + managerWorldOffset) != evidence.WorldInfo))
                        throw new InvalidDataException("Lifecycle manager binding changed during the inventory call.");
                    if (operation == LifecycleOperation.LoadDefaultInventory)
                    {
                        uint before = reader.U32(commandAddress + LifecycleCallHook.InventoryPolicyBefore);
                        uint after = reader.U32(commandAddress + LifecycleCallHook.InventoryPolicyAfter);

                        if ((before != 0 && before != inventoryPolicyMask) || after != before)
                            throw new InvalidDataException("Bot 默认装备加载改变了共享武器查找策略。");
                        save("inventory-policy-preserved", new ExecuteAsyncJson2 { Manager = manager, inventoryPolicyOffset = inventoryPolicyOffset,
                            inventoryPolicyMask = inventoryPolicyMask, Before = before, After = after });
                    }

                    QueryObject? spawned = null;

                    if (operation == LifecycleOperation.AllocatePlayerIdentity)
                    {
                        uint executedSequence = reader.U32(commandAddress + 0xE8);
                        uint currentSequence = reader.U32(0x01FA8E50);

                        if (sample.Result < 0 || unchecked((uint)sample.Result) != executedSequence ||
                            reader.U32(playerIdAddress) != executedSequence || currentSequence < executedSequence + 1 ||
                            (reader.U32(botBitAddress) & botBitMask) == 0 || reader.U32(commandAddress + 0xE4) != 1 ||
                            (preparationCommand == 0 && (executedSequence != previousSequence ||
                                currentSequence != previousSequence + 1 || reader.U32(numPlayersAddress) != 0 ||
                                reader.U32(numBotsAddress) != 0)))
                            throw new InvalidDataException("Bot 原生身份分配及精确 PRI 绑定缺少完整证据。");

                        if (preparationCommand != 0)
                            AfdevBioBotPreparationInspector.RequirePreparing(process, evidence, preparationCommand);
                    }
                    if (borrowedName != null)
                    {
                        uint nameAddress = reader.FieldAddress(pri, "PlayerName", "Engine.PlayerReplicationInfo.PlayerName", "StrProperty", 12);
                        uint nameData = reader.U32(nameAddress);
                        uint nameCount = reader.U32(nameAddress + 4);

                        if (nameData == commandAddress + 0xE00 || nameData < 0x10000 || nameCount * 2 != borrowedName.Length || reader.U32(nameAddress + 8) < nameCount || !process.ReadMemory(nameData, borrowedName.Length).AsSpan().SequenceEqual(borrowedName) || !process.ReadMemory(commandAddress + 0xE00, borrowedName.Length).AsSpan().SequenceEqual(borrowedName) || !process.ReadMemory(parms, parameters.Length).AsSpan().SequenceEqual(parameters))
                            throw new InvalidDataException("原生昵称未形成独立引擎 FString 或借用输入发生变化。");
                        save("lifecycle-name-ownership", new ExecuteAsyncJson3 { nameAddress = nameAddress, nameData = nameData, nameCount = nameCount, Nickname = nickname, IndependentEngineString = true, BorrowedInput = commandAddress + 0xE00 });
                    }

                    if (operation == LifecycleOperation.SpawnInventoryManager)
                    {
                        spawned = reader.Identity(unchecked((uint)sample.Result));
                        uint managerResult = reader.U32(commandAddress + 0xE4);

                        if (sample.Result != sample.ParmsValue || spawned.Class != managerClass ||
                            reader.U32(managerField) != spawned.Address ||
                            (managerResult != 1 && (preparationCommand == 0 || managerResult != 2)) ||
                            reader.U32(spawned.Address + managerOwnerOffset) != evidence.GameInfo ||
                            reader.U32(spawned.Address + managerWorldOffset) != evidence.WorldInfo)
                            throw new InvalidDataException("Native manager binding lacks exact returned owner/world/class evidence.");

                        save("lifecycle-manager-ownership", new ExecuteAsyncJson4
                        {
                            Manager = spawned, ReusedExistingManager = managerResult == 2,
                            NativeSpawnInvoked = managerResult == 1, World = evidence.World, GameInfo = evidence.GameInfo
                        });
                    }

                    var result = new LifecycleCallResult(operation, ordinal, sample, spawned, method);

                    save("lifecycle-completed-" + operation, result);
                    report("LIFECYCLE_COMPLETED " + operation + " ordinal=" + ordinal);
                    return result;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("Lifecycle deadline elapsed; blocked or started commands are never retried.");
        }
        finally
        {
            save("lifecycle-observations-" + operation, observations);
            if (process.IsAlive)
                save("lifecycle-final-" + operation, new ExecuteAsyncJson5 { Observation = QueryRun.Read(process, commandAddress, resultAddress), CompletedOrdinal = reader.U32(commandAddress + LifecycleCallHook.CompletedOrdinal), CommandHex = Convert.ToHexString(process.ReadMemory(commandAddress, LifecycleCallHook.CommandSize)) });
        }
    }

    private static IReadOnlyList<QueryObject> IdentityClosure(QueryInspector reader, IEnumerable<uint> roots)
    {
        var identities = new List<QueryObject>();
        var pending = new Queue<uint>(roots);
        var seen = new HashSet<uint>();

        while (pending.TryDequeue(out uint pointer))
        {
            if (pointer == 0 || !seen.Add(pointer))
                continue;
            if (seen.Count > 64)
                throw new InvalidDataException("Lifecycle identity closure exceeds the fixed 64-object bound.");

            QueryObject identity = reader.Identity(pointer);

            if ((reader.U32(pointer + 0xC) & 0x20000000) != 0)
                throw new InvalidDataException("Lifecycle identity is pending kill.");
            identities.Add(identity);
            pending.Enqueue(identity.Outer);
            pending.Enqueue(identity.Class);
        }

        return identities;
    }

    private static uint FindClass(AfdevOwnedProcess process, QueryInspector reader, string path, uint coreClass)
    {
        uint count = reader.U32(0x02053EAC), data = reader.U32(0x02053EA8);

        if (count == 0 || count > 1000000 || data == 0)
            throw new InvalidDataException("Class scan object table differs.");

        byte[] table = process.ReadMemory(data, checked((int)count * 4));
        uint found = 0;

        for (int i = 0; i < count; i++)
        {
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4, 4));

            if (pointer == 0)
                continue;
            if (reader.U32(pointer + 0x34) != coreClass || reader.U32(pointer + 0x30) != 0)
                continue;
            if (reader.Path(pointer) != path)
                continue;
            reader.Identity(pointer);
            if (found != 0)
                throw new InvalidDataException("Exact lifecycle class is ambiguous.");
            found = pointer;
        }

        return found != 0 ? found : throw new MissingFieldException(path);
    }
}

sealed record LifecycleProof(uint Address, uint Expected, uint Mask);
sealed record LifecycleCallResult(LifecycleOperation Operation, uint Ordinal, QueryObservation Observation, QueryObject? SpawnedManager, ResourceFunction Function);
