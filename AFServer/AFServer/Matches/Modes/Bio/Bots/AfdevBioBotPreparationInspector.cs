using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Freezes this round's lightweight preparation boundary before publishing human endpoints, without
/// creating Controllers, resources, or Pawns.
/// </summary>
internal sealed class AfdevBioBotPreparationInspector(AfdevOwnedProcess process)
{
    public byte[] Describe(AfdevNativeEvidence world, AfdevBioBotPlan plan, uint command,
        uint startEntry, uint timerEntry, IReadOnlyList<uint> activationCommands)
    {
        plan.Validate();

        if (plan.Bots.Length == 0 || plan.Bots.Length > activationCommands.Count ||
            plan.AllocationId != world.AllocationId || plan.FighterCapacity != world.FighterCapacity ||
            world.ProcessId != process.ProcessId || world.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            world.WorldGeneration != 1 || !AfdevMapCatalog.MatchesBio(world))
            throw new InvalidDataException("准备协调页不属于当前普通 BIO 世界及冻结名册。");

        var reader = new QueryInspector(process);
        var proofs = new Dictionary<uint, uint>();
        byte[] result = new byte[AfdevBioBotPreparationHook.PageSize];

        void Put(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan((int)offset), value);
        void Proof(uint address) => proofs.TryAdd(address, reader.U32(address));
        uint Field(uint instance, string path, string kind = "IntProperty", uint size = 4)
        {
            uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, kind, size);
            uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);

            foreach (uint offset in new uint[] { 0x28, 0x34, 0x40, 0x44, 0x60 })
                Proof(property + offset);

            return address;
        }

        void Bit(string path, uint addressSlot, uint maskSlot, bool expected)
        {
            uint address = Field(world.GameInfo, path, "BoolProperty");
            uint property = reader.FindField(world.GameInfoClass, path.Split('.')[^1], path);
            uint mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0 || ((reader.U32(address) & mask) != 0) != expected)
                throw new InvalidDataException("准备协调页的等待或热身条件不符合原生准备阶段。");

            Proof(property + 0x84);
            Put(addressSlot, address);
            Put(maskSlot, mask);
        }

        uint frame = reader.U32(world.GameInfo + 0x18);
        uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);

        if (state == 0 || reader.Path(state) != "UTGame.TGGame.PendingMatch" ||
            reader.U32(frame + 0x14) != world.GameInfo || reader.U32(0x02066BF8) != world.World ||
            reader.Path(world.GameInfoClass) != "TGBioGame.TGBioMatch" ||
            reader.U32(world.World + 0xD8) != world.NetDriver || reader.U32(world.NetDriver + 0x48) != 0)
            throw new InvalidDataException("准备门必须在当前 PendingMatch、真人端点尚未发布时安装。");

        ResourceFunction start = new ResourceFunctionCatalog(process).DescribeStartMatch(world.GameInfo);
        uint timer = reader.FindField(state, "Timer", "UTGame.TGGame.PendingMatch.Timer");

        if (start.Path != "Engine.GameInfo.PendingMatch.StartMatch" ||
            start.ExecPointer != AfdevBioBotTimerHook.OriginalExec ||
            reader.U32(timer + 0xC8) != AfdevBioBotTimerHook.OriginalExec ||
            reader.Path(reader.U32(timer + 0x34)) != "Core.Function" || reader.U32(timer + 0xAC) != 0x20002 ||
            reader.U16(timer + 0xB0) != 0 || reader.U16(timer + 0xBE) != 0 ||
            reader.U16(timer + 0xC0) != 0xFFFF || reader.U32(timer + 0x58) != 2927)
            throw new InvalidDataException("原生 BIO 准备 Timer/StartMatch 的精确方法与审核布局不一致。");

        foreach (uint function in new[] { start.Function.Address, timer })
            foreach (uint offset in new uint[] { 0x28, 0x34, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0xAC, 0xB0, 0xBC, 0xC0 })
                Proof(function + offset);

        Put(AfdevBioBotPreparationHook.BotCount, (uint)plan.Bots.Length);
        Put(AfdevBioBotPreparationHook.HumanCount, (uint)plan.Humans.Count(human => !human.Observer));
        Put(AfdevBioBotPreparationHook.ObserverCount, (uint)plan.Humans.Count(human => human.Observer));
        Put(AfdevBioBotPreparationHook.FighterCapacity, (uint)plan.FighterCapacity);
        Put(AfdevBioBotPreparationHook.RegistrationCounter,
            activationCommands[0] + AfdevBioBotActivationHook.RegisteredCount);
        Put(AfdevBioBotPreparationHook.World, world.World);
        Put(AfdevBioBotPreparationHook.WorldInfo, world.WorldInfo);
        Put(AfdevBioBotPreparationHook.Game, world.GameInfo);
        Put(AfdevBioBotPreparationHook.Engine, reader.U32(0x02063820));
        Put(AfdevBioBotPreparationHook.Thread, process.MainThreadId);
        Put(AfdevBioBotPreparationHook.NetDriver, world.NetDriver);
        Put(AfdevBioBotPreparationHook.PendingState, state);
        Put(AfdevBioBotPreparationHook.StartFunction, start.Function.Address);
        Put(AfdevBioBotPreparationHook.StartOriginal, AfdevBioBotTimerHook.OriginalExec);
        Put(AfdevBioBotPreparationHook.StartEntry, startEntry);
        Put(AfdevBioBotPreparationHook.TimerFunction, timer);
        Put(AfdevBioBotPreparationHook.TimerOriginal, AfdevBioBotTimerHook.OriginalExec);
        Put(AfdevBioBotPreparationHook.TimerEntry, timerEntry);
        Put(AfdevBioBotPreparationHook.Self, command);

        uint countSlot = AfdevBioBotPreparationHook.NumPlayers;

        foreach (string name in new[] { "NumPlayers", "NumBots", "NumTravellingPlayers", "NumSpectators" })
        {
            uint address = Field(world.GameInfo, "Engine.GameInfo." + name);

            if (reader.U32(address) != 0)
                throw new InvalidDataException("首次准备门安装前出现原生参赛者，不能发布不完整名册。");

            Put(countSlot, address);
            countSlot += 4;
        }

        Put(AfdevBioBotPreparationHook.MaxPlayers, Field(world.GameInfo, "Engine.GameInfo.MaxPlayers"));
        Put(AfdevBioBotPreparationHook.MinNetPlayers, Field(world.GameInfo, "UTGame.UTGame.MinNetPlayers"));
        Bit("Engine.GameInfo.bWaitingToStartMatch", AfdevBioBotPreparationHook.WaitingAddress,
            AfdevBioBotPreparationHook.WaitingMask, true);
        Bit("UTGame.UTGame.bWarmupRound", AfdevBioBotPreparationHook.WarmupAddress,
            AfdevBioBotPreparationHook.WarmupMask, false);
        Put(AfdevBioBotPreparationHook.NetModeAddress,
            Field(world.WorldInfo, "Engine.WorldInfo.NetMode", "ByteProperty", 1));

        for (int index = 0; index < plan.Bots.Length; index++)
            Put(AfdevBioBotPreparationHook.ActivationCommands + (uint)index * 4, activationCommands[index]);

        QueryObject[] identities = reader.IdentityClosure(world.World, world.WorldInfo, world.GameInfo,
            state, start.Function.Address, timer).ToArray();

        if (identities.Length is 0 or > AfdevBioBotPreparationHook.IdentityCapacity ||
            proofs.Count is 0 or > AfdevBioBotPreparationHook.ProofCapacity)
            throw new InvalidDataException("BIO 准备协调的精确身份或反射证明超出固定页预算。");

        Put(AfdevBioBotPreparationHook.IdentityCount, (uint)identities.Length);
        Put(AfdevBioBotPreparationHook.ProofCount, (uint)proofs.Count);

        for (int index = 0; index < identities.Length; index++)
        {
            QueryObject identity = identities[index];
            uint[] fields = [identity.Address, identity.Index, identity.Outer, identity.NameIndex,
                identity.NameNumber, identity.Class];

            for (int field = 0; field < fields.Length; field++)
                Put(AfdevBioBotPreparationHook.Identities + (uint)(index * 24 + field * 4), fields[field]);
        }

        uint proofSlot = AfdevBioBotPreparationHook.Proofs;

        foreach (var proof in proofs)
        {
            Put(proofSlot, proof.Key);
            Put(proofSlot + 4, proof.Value);
            proofSlot += 8;
        }

        return result;
    }

    /// <summary>
    /// Managed readings provide early rejection only. Actual command consumption must recheck the
    /// game-thread preparation gate.
    /// </summary>
    public static void RequirePreparing(AfdevOwnedProcess process, AfdevNativeEvidence world, uint command)
    {
        var reader = new QueryInspector(process);

        uint Read(uint offset) => reader.U32(command + offset);
        uint Count(uint offset) => reader.U32(Read(offset));

        if (command < 0x10000 || (command & 3) != 0 || Read(AfdevBioBotPreparationHook.Self) != command ||
            Read(AfdevBioBotPreparationHook.Request) != 1 ||
            Read(AfdevBioBotPreparationHook.Status) != AfdevBioBotPreparationHook.Installed ||
            Read(AfdevBioBotPreparationHook.World) != world.World ||
            Read(AfdevBioBotPreparationHook.Game) != world.GameInfo ||
            Read(AfdevBioBotPreparationHook.NetDriver) != world.NetDriver)
            throw new InvalidDataException("Bot 原生命令没有本局已安装且未放行的准备协调页。");

        uint registered = Count(AfdevBioBotPreparationHook.RegistrationCounter);
        uint humans = Read(AfdevBioBotPreparationHook.HumanCount);
        uint players = Count(AfdevBioBotPreparationHook.NumPlayers);

        // Multiple C# reads may straddle atomic game-thread registration. BuildGuard checks paired differences at consumption time.
        if (registered > Read(AfdevBioBotPreparationHook.BotCount) || players > (uint)world.FighterCapacity ||
            Count(AfdevBioBotPreparationHook.NumBots) != 0 ||
            Count(AfdevBioBotPreparationHook.NumTravelling) > humans ||
            Count(AfdevBioBotPreparationHook.NumSpectators) > Read(AfdevBioBotPreparationHook.ObserverCount) ||
            Count(AfdevBioBotPreparationHook.MaxPlayers) != (uint)world.FighterCapacity ||
            reader.U32(world.NetDriver + 0x48) > humans + Read(AfdevBioBotPreparationHook.ObserverCount))
            throw new InvalidDataException("Bot 准备阶段的原生计数或连接越过冻结真人及 Bot 容量。");
    }
}
