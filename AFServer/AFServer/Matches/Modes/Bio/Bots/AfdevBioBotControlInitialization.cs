using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Builds one control-initialization descriptor for controlled BIO comparisons and awaits its game-thread
/// receipt. This option does not establish combat or complete-navigation acceptance. The caller serializes
/// the same instance; pages remain until owned-Job exit, and unknown completion forbids resending.
/// </summary>
internal static partial class AfdevBioBotControlInitialization
{
    /// <summary>
    /// Allowed only after final internal-identity confirmation and before player-count registration or
    /// Pawn/equipment creation.
    /// </summary>
    public static async Task<AfdevBioBotControlReceipt> ExecuteAsync(AfdevOwnedProcess process,
        AfdevNativeEvidence world, uint controller, Guid botId, string uin, uint onlinePage, uint command,
        Action<string, object> save, Action checkBinding, CancellationToken token = default, uint preparationCommand = 0)
    {
        token.ThrowIfCancellationRequested();
        checkBinding();

        var reader = new QueryInspector(process);

        if (command < 0x10000 || reader.U32(command + AfdevBioBotControlHook.Published) != 0 ||
            reader.U32(command + AfdevBioBotControlHook.Request) != 0 ||
            reader.U32(command + AfdevBioBotControlHook.Started) != 0 ||
            reader.U32(command + AfdevBioBotControlHook.Returned) != 0)
            throw new InvalidOperationException("本实例的 BIO 控制初始化已发布或页面无效，不能重试。");

        byte[] descriptor = Describe(process, world, controller, botId, uin, onlinePage, preparationCommand);

        save("control-initialization-request", new ExecuteAsyncJson1
        {
            InstanceId = world.InstanceId, AttemptId = world.AttemptId, AllocationId = world.AllocationId, WorldGeneration = world.WorldGeneration,
            ProcessId = world.ProcessId, ProcessCreationTimeUtc = world.ProcessCreationTimeUtc, AfdevSha256 = world.AfdevSha256, controller = controller, botId = botId, uin = uin,
            onlinePage = onlinePage, command = command, preparationCommand = preparationCommand, DescriptorHex = Convert.ToHexString(descriptor),
            Basis = "Dedicated TGRealGun/近战原生 bIsRobot 分支；精确未登记 BIO PC 实例初始化",
            ControlledComparisonOnly = true
        });
        process.WriteMemory(command, descriptor);

        byte[] installed = process.ReadMemory(command, descriptor.Length);

        // The idle gate increments only its own IdleAck. It is not a descriptor field, so concurrent Ticks must not imply installation failure.
        if (!installed.AsSpan(0, (int)AfdevBioBotControlHook.IdleAck).SequenceEqual(
                descriptor.AsSpan(0, (int)AfdevBioBotControlHook.IdleAck)) ||
            !installed.AsSpan((int)AfdevBioBotControlHook.IdleAck + 4).SequenceEqual(
                descriptor.AsSpan((int)AfdevBioBotControlHook.IdleAck + 4)))
            throw new IOException("BIO 控制初始化描述安装回读不一致，必须回收原 Job。");

        token.ThrowIfCancellationRequested();
        checkBinding();
        process.WriteMemory(command + AfdevBioBotControlHook.Request, [1, 0, 0, 0]);

        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            checkBinding();

            AfdevBioBotControlReceipt receipt = Read(process, command);

            if (receipt.Returned == 1 && receipt.Request == 0)
            {
                bool countsValid = preparationCommand == 0
                    ? receipt.BeforeCounts.All(count => count == 0) && receipt.AfterCounts.All(count => count == 0)
                    : receipt.BeforeCounts.SequenceEqual(receipt.AfterCounts) && receipt.BeforeCounts[1] == 0;

                save("control-initialization-returned", receipt);

                if (receipt.Started != 1 || receipt.Status != AfdevBioBotControlHook.Success || receipt.Applied != 1 ||
                    receipt.AfterWord != (receipt.BeforeWord | 0x40000) ||
                    receipt.BeforePawn != 0 || receipt.AfterPawn != 0 ||
                    receipt.PreparationCommand != preparationCommand || !countsValid)
                    throw new InvalidDataException($"BIO 控制初始化未成功，状态 {receipt.Status}；禁止重发，回收原 Job。");

                if (preparationCommand != 0)
                    AfdevBioBotPreparationInspector.RequirePreparing(process, world, preparationCommand);

                AfdevBioBotControlReceipt completed = await DisarmAndWaitAsync(process, command, token).ConfigureAwait(false);

                save("control-initialization-idle", completed);
                checkBinding();
                return completed;
            }

            if (receipt.Returned > 1 || receipt.Started > 1 || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("BIO 控制初始化完成未知，禁止重发，必须回收原 Job。");

            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Revokes the request first, then waits for the same game thread to cross the idle gate. Preserves
    /// latched terminal state.
    /// </summary>
    public static async Task<AfdevBioBotControlReceipt> DisarmAndWaitAsync(AfdevOwnedProcess process,
        uint command, CancellationToken token = default)
    {
        AfdevBioBotControlReceipt initial = Read(process, command);

        if (!process.IsAlive || initial.Published == 0)
            return initial;

        var elapsed = Stopwatch.StartNew();

        process.WriteMemory(command + AfdevBioBotControlHook.Request, [0, 0, 0, 0]);

        while (process.IsAlive)
        {
            token.ThrowIfCancellationRequested();

            AfdevBioBotControlReceipt current = Read(process, command);

            if (current.IdleAck != initial.IdleAck)
            {
                if (current.Started != 0 && (current.Started != 1 || current.Returned != 1 ||
                        current.Status != AfdevBioBotControlHook.Success))
                    throw new IOException("BIO 控制门已进入但没有成功终态，必须回收原 Job。");

                return current;
            }

            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("BIO 控制门未确认空闲，必须回收原 Job。");

            await Task.Delay(10, token).ConfigureAwait(false);
        }

        return initial;
    }

    private static byte[] Describe(AfdevOwnedProcess process, AfdevNativeEvidence world,
        uint controller, Guid botId, string uin, uint onlinePage, uint preparationCommand)
    {
        var reader = new QueryInspector(process);
        byte[] command = new byte[AfdevBioBotControlHook.PageSize];
        var proofs = new Dictionary<uint, (uint Value, uint Mask)>();

        void Put(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan((int)offset), value);
        void Proof(uint address, uint? expected = null, uint mask = uint.MaxValue)
        {
            uint actual = reader.U32(address) & mask;
            uint value = expected ?? actual;

            if (actual != value || proofs.TryGetValue(address, out var previous) && previous != (value, mask))
                throw new InvalidDataException("BIO 控制初始化的反射关系或冻结条件不一致。");

            proofs[address] = (value, mask);
        }

        uint Field(uint instance, string path, string kind = "ObjectProperty", uint size = 4)
        {
            uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, kind, size);
            uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);

            foreach (uint offset in new uint[] { 4, 0x28, 0x2C, 0x30, 0x34, 0x40, 0x44, 0x4C, 0x50, 0x60 })
                Proof(property + offset);

            return address;
        }

        (uint Address, uint Mask) Bit(uint instance, string path)
        {
            uint address = Field(instance, path, "BoolProperty");
            uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);
            uint mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("BIO 控制初始化的 BoolProperty 不是单个位。");

            Proof(property + 0x84);
            return (address, mask);
        }

        if (botId == Guid.Empty || uin != "bot-" + botId.ToString("N")[..12] ||
            world.InstanceId == Guid.Empty || world.AttemptId == Guid.Empty || world.AllocationId == Guid.Empty ||
            world.WorldGeneration != 1 || !AfdevMapCatalog.MatchesBio(world) ||
            world.ProcessId != process.ProcessId || world.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            onlinePage < 0x10000 || reader.U32(0x02066BF8) != world.World)
            throw new InvalidDataException("BIO 控制初始化没有本次实例、世界和最终内部身份的精确绑定。");

        if (preparationCommand != 0)
            AfdevBioBotPreparationInspector.RequirePreparing(process, world, preparationCommand);

        uint priAddress = Field(controller, "Engine.Controller.PlayerReplicationInfo");
        uint pri = reader.U32(priAddress);
        uint gameFrame = reader.U32(world.GameInfo + 0x18);
        uint pcFrame = reader.U32(controller + 0x18);
        uint gameState = gameFrame == 0 ? 0 : reader.U32(gameFrame + 0x28);
        uint pcState = pcFrame == 0 ? 0 : reader.U32(pcFrame + 0x28);

        if (reader.Path(reader.Identity(controller).Class) != "TGBioGame.TGBioPlayerController" ||
            reader.Path(reader.Identity(pri).Class) != "TGBioGame.TGBioPlayerReplicationInfo" ||
            gameState == 0 || reader.Path(gameState) != "UTGame.TGGame.PendingMatch" ||
            pcState == 0 || reader.Path(pcState) != "UTGame.TGPlayerController.PlayerWaiting")
            throw new InvalidDataException("BIO 控制初始化仅支持原生 PlayerWaiting 中尚未开局的精确 BIO PC。");

        byte[] online = process.ReadMemory(onlinePage, AfdevOnlineDsIsolationHook.PageSize);
        byte[] expectedOnline = AfdevOnlineDsIsolationHook.Describe(process, world, controller, botId, uin);

        foreach ((int offset, int length) in new[] { (0x40, 0x50), (0x100, 0x50), (0x200, 0x100) })
            if (!online.AsSpan(offset, length).SequenceEqual(expectedOnline.AsSpan(offset, length)))
                throw new InvalidDataException("BIO 控制初始化与已经确认的 OnlineDS 完整身份不一致。");

        if (BinaryPrimitives.ReadUInt32LittleEndian(online) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(online.AsSpan(4)) != 0)
            throw new InvalidDataException("BIO 控制初始化要求 OnlineDS 最终身份 Active=1 且无故障。");

        var robot = Bit(controller, "Engine.Controller.bIsRobot");
        var waiting = Bit(world.GameInfo, "Engine.GameInfo.bWaitingToStartMatch");
        var warmup = Bit(world.GameInfo, "UTGame.UTGame.bWarmupRound");
        uint pawnAddress = Field(controller, "Engine.Controller.Pawn");
        uint playerAddress = Field(controller, "Engine.PlayerController.Player");
        uint uinAddress = Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);

        if (robot.Address != controller + 0x1E8 || robot.Mask != 0x40000 ||
            (reader.U32(robot.Address) & robot.Mask) != 0)
            throw new InvalidDataException("BIO 控制位反射与已核原生来源不一致，或此实例已初始化。");

        Proof(priAddress, pri);
        Proof(Field(controller, "Engine.Actor.WorldInfo"), world.WorldInfo);
        Proof(Field(pri, "Engine.Actor.WorldInfo"), world.WorldInfo);
        Proof(Field(pri, "Engine.Actor.Owner"), controller);
        Proof(Field(world.WorldInfo, "Engine.WorldInfo.Game"), world.GameInfo);
        Proof(pawnAddress, 0);
        Proof(playerAddress, 0);
        Proof(waiting.Address, waiting.Mask, waiting.Mask);
        Proof(warmup.Address, 0, warmup.Mask);
        Proof(world.World + 0xD8, world.NetDriver);
        Proof(world.GameInfo + 0x18, gameFrame);
        Proof(controller + 0x18, pcFrame);
        Proof(gameFrame + 0x14, world.GameInfo);
        Proof(pcFrame + 0x14, controller);
        Proof(gameFrame + 0x28, gameState);
        Proof(pcFrame + 0x28, pcState);

        if (world.NetDriver < 0x10000 || preparationCommand == 0 && reader.U32(world.NetDriver + 0x48) != 0)
            throw new InvalidDataException("BIO 控制初始化需要有效驱动；未绑定准备页时仍须处于空连接窗口。");

        string[] countNames = ["NumPlayers", "NumBots", "NumTravellingPlayers", "NumSpectators"];

        for (uint index = 0; index < countNames.Length; index++)
        {
            uint field = Field(world.GameInfo, "Engine.GameInfo." + countNames[index], "IntProperty");

            if ((preparationCommand == 0 || countNames[index] == "NumBots") && reader.U32(field) != 0)
                throw new InvalidDataException("BIO 控制初始化的 NumBots 必须为零；未绑定准备页时四项人数均须为零。");

            Put(AfdevBioBotControlHook.CountAddresses + index * 4, field);
        }

        uint thread = reader.U32(0x01FE22C0);
        uint engine = reader.U32(0x02063820);
        QueryObject[] identities = reader.IdentityClosure(world.World, world.WorldInfo, world.GameInfo,
            controller, pri, gameState, pcState, engine).ToArray();

        if (thread == 0 || identities.Length > AfdevBioBotControlHook.IdentityCapacity ||
            proofs.Count > AfdevBioBotControlHook.ProofCapacity)
            throw new InvalidDataException("BIO 控制初始化的线程或反射描述超出固定页面边界。");

        foreach (uint offset in new uint[] { AfdevBioBotControlHook.BeforeWord, AfdevBioBotControlHook.AfterWord,
            AfdevBioBotControlHook.BeforePawn, AfdevBioBotControlHook.AfterPawn,
            AfdevBioBotControlHook.BeforeState, AfdevBioBotControlHook.AfterState })
            Put(offset, uint.MaxValue);

        for (uint index = 0; index < 4; index++)
        {
            Put(AfdevBioBotControlHook.BeforeCounts + index * 4, uint.MaxValue);
            Put(AfdevBioBotControlHook.AfterCounts + index * 4, uint.MaxValue);
        }

        Put(AfdevBioBotControlHook.Published, 1);
        Put(AfdevBioBotControlHook.ThreadId, thread);
        Put(AfdevBioBotControlHook.Engine, engine);
        Put(AfdevBioBotControlHook.World, world.World);
        Put(AfdevBioBotControlHook.WorldInfo, world.WorldInfo);
        Put(AfdevBioBotControlHook.Game, world.GameInfo);
        Put(AfdevBioBotControlHook.Controller, controller);
        Put(AfdevBioBotControlHook.Pri, pri);
        Put(AfdevBioBotControlHook.OnlinePage, onlinePage);
        Put(AfdevBioBotControlHook.GameFrame, gameFrame);
        Put(AfdevBioBotControlHook.GameState, gameState);
        Put(AfdevBioBotControlHook.ControllerFrame, pcFrame);
        Put(AfdevBioBotControlHook.ControllerState, pcState);
        Put(AfdevBioBotControlHook.NetDriver, world.NetDriver);
        Put(AfdevBioBotControlHook.RobotAddress, robot.Address);
        Put(AfdevBioBotControlHook.RobotMask, robot.Mask);
        Put(AfdevBioBotControlHook.PawnAddress, pawnAddress);
        Put(AfdevBioBotControlHook.PlayerAddress, playerAddress);
        Put(AfdevBioBotControlHook.UinAddress, uinAddress);
        Put(AfdevBioBotControlHook.UinCount, (uint)uin.Length + 1);
        Put(AfdevBioBotControlHook.IdentityCount, (uint)identities.Length);
        Put(AfdevBioBotControlHook.ProofCount, (uint)proofs.Count);
        Put(AfdevBioBotControlHook.PreparationPage, preparationCommand);
        online.AsSpan(0x100, 0x50).CopyTo(command.AsSpan((int)AfdevBioBotControlHook.IdentityBinding));
        online.AsSpan(0x40, 0x50).CopyTo(command.AsSpan((int)AfdevBioBotControlHook.OnlineDescription));
        Encoding.Unicode.GetBytes(uin + '\0').CopyTo(command, (int)AfdevBioBotControlHook.ExpectedUin);

        for (int index = 0; index < identities.Length; index++)
        {
            QueryObject id = identities[index];
            uint[] values = [id.Address, id.Index, id.Outer, id.NameIndex, id.NameNumber, id.Class];

            for (int field = 0; field < values.Length; field++)
                Put(AfdevBioBotControlHook.Identities + (uint)(index * 24 + field * 4), values[field]);
        }

        int proofIndex = 0;

        foreach (var proof in proofs)
        {
            uint offset = AfdevBioBotControlHook.Proofs + (uint)(proofIndex++ * 12);

            Put(offset, proof.Key);
            Put(offset + 4, proof.Value.Value);
            Put(offset + 8, proof.Value.Mask);
        }

        return command;
    }

    private static AfdevBioBotControlReceipt Read(AfdevOwnedProcess process, uint command)
    {
        byte[] bytes = process.ReadMemory(command, AfdevBioBotControlHook.PageSize);

        uint Read(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset, 4));
        uint[] Counts(uint start) => Enumerable.Range(0, 4).Select(index => Read(start + (uint)index * 4)).ToArray();

        return new AfdevBioBotControlReceipt(DateTimeOffset.UtcNow, command,
            new Guid(bytes.AsSpan(0x100, 16)), new Guid(bytes.AsSpan(0x110, 16)),
            new Guid(bytes.AsSpan(0x120, 16)), new Guid(bytes.AsSpan(0x130, 16)),
            Read(AfdevBioBotControlHook.World), Read(AfdevBioBotControlHook.Controller), Read(AfdevBioBotControlHook.Pri),
            Read(AfdevBioBotControlHook.Request), Read(AfdevBioBotControlHook.Published),
            Read(AfdevBioBotControlHook.Started), Read(AfdevBioBotControlHook.Returned), Read(AfdevBioBotControlHook.Status),
            Read(AfdevBioBotControlHook.IdleAck), Read(AfdevBioBotControlHook.Applied),
            Read(AfdevBioBotControlHook.BeforeWord), Read(AfdevBioBotControlHook.AfterWord),
            Read(AfdevBioBotControlHook.BeforeThread), Read(AfdevBioBotControlHook.AfterThread),
            Read(AfdevBioBotControlHook.BeforePawn), Read(AfdevBioBotControlHook.AfterPawn),
            Read(AfdevBioBotControlHook.BeforeState), Read(AfdevBioBotControlHook.AfterState),
            Counts(AfdevBioBotControlHook.BeforeCounts), Counts(AfdevBioBotControlHook.AfterCounts),
            Read(AfdevBioBotControlHook.PreparationPage));
    }
}

/// <summary>
/// Independent receipt for exactly one initialization. Applied proves only the bit write, not weapon,
/// navigation, or human-play acceptance.
/// </summary>
internal sealed record AfdevBioBotControlReceipt(DateTimeOffset CapturedAtUtc, uint Command,
    Guid InstanceId, Guid AttemptId, Guid AllocationId, Guid BotId, uint World, uint Controller, uint Pri,
    uint Request, uint Published, uint Started, uint Returned, uint Status, uint IdleAck, uint Applied,
    uint BeforeWord, uint AfterWord, uint BeforeThread, uint AfterThread,
    uint BeforePawn, uint AfterPawn, uint BeforeState, uint AfterState,
    IReadOnlyList<uint> BeforeCounts, IReadOnlyList<uint> AfterCounts, uint PreparationCommand);
