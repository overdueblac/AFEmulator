using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Frozen activation-gate reflection layout. Connections are not UObjects and are obtained from the current
/// Driver only on the game thread.
/// </summary>
internal sealed class AfdevBioBotActivationInspector(AfdevOwnedProcess process)
{
    public byte[] Describe(AfdevNativeEvidence world, AfdevBioBotPreparation bot, AfdevBioBotHumanIdentity human) =>
        Describe(world, bot, [human], 2, 1, 0, 0, 0);

    /// <summary>
    /// Freezes this slot after serialized preparation of the complete roster. Human identities retain
    /// session provenance; the native gate still checks PC/PRI through current connections. Shared counts
    /// allow each bot to register once in order. If Timer is installed, accept only the same host-generated
    /// entry.
    /// </summary>
    public byte[] Describe(AfdevNativeEvidence world, AfdevBioBotPreparation bot,
        IReadOnlyList<AfdevBioBotHumanIdentity> humans, int fighterCapacity, int botCount,
        uint timerEntry, uint registrationCounter, int botIndex, uint preparationCommand = 0)
    {
        var r = new QueryInspector(process);
        byte[] command = new byte[AfdevBioBotActivationHook.PageSize];
        var proofs = new Dictionary<uint, uint>();
        AfdevBioBotHumanIdentity[] fighters = humans.Where(human => !human.Observer).ToArray();

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(offset), value);
        void Proof(uint address) => proofs.Add(address, r.U32(address));
        uint Field(uint instance, string path, string kind = "ObjectProperty", uint size = 4)
        {
            uint address = r.FieldAddress(instance, path.Split('.')[^1], path, kind, size);
            uint property = r.FindField(r.U32(instance + 0x34), path.Split('.')[^1], path);

            foreach (uint offset in new uint[] { 0x28, 0x34, 0x40, 0x44, 0x60 })
                proofs.TryAdd(property + offset, r.U32(property + offset));

            return address;
        }

        (uint Offset, uint Mask) Bit(uint instance, string path)
        {
            uint address = Field(instance, path, "BoolProperty");
            uint property = r.FindField(r.U32(instance + 0x34), path.Split('.')[^1], path);
            uint mask = r.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("激活门布尔属性掩码无效。");

            proofs.TryAdd(property + 0x84, mask);
            return (address - instance, mask);
        }

        if (humans.Count is 0 or > 32 || fighters.Length == 0 ||
            humans.Any(human => human.SessionId == Guid.Empty || human.Uin == 0 || string.IsNullOrWhiteSpace(human.Nickname)) ||
            humans.Select(human => human.SessionId).Distinct().Count() != humans.Count ||
            humans.Select(human => human.Uin).Distinct().Count() != humans.Count ||
            fighterCapacity is < 2 or > 16 || botCount is < 1 or > 15 || fighters.Length + botCount != fighterCapacity ||
            botIndex < 0 || botIndex >= botCount ||
            (registrationCounter != 0 && (registrationCounter < 0x10000 || (registrationCounter & 3) != 0)) ||
            (botCount > 1 && registrationCounter == 0) ||
            bot.World != world.World || bot.GameInfo != world.GameInfo || bot.AllocationId != world.AllocationId ||
            bot.AttemptId != world.AttemptId || bot.InstanceId != world.InstanceId ||
            bot.WorldGeneration != world.WorldGeneration || bot.ProcessId != world.ProcessId ||
            bot.ProcessCreationTimeUtc != world.ProcessCreationTimeUtc || r.U32(0x02066BF8) != world.World)
            throw new InvalidDataException("激活描述器与冻结世界、真人或 Bot 不一致。");

        uint pcClass = r.Identity(bot.Controller).Class;
        uint priClass = r.Identity(bot.Pri).Class;
        uint frame = r.U32(world.GameInfo + 0x18);
        uint state = frame == 0 ? 0 : r.U32(frame + 0x28);
        AfdevBioBotWaitingEvidence waiting = new AfdevBioBotWaitingInspector(process).Capture(world, bot.Controller);

        if (!waiting.Ready.Value)
            throw new InvalidOperationException("原生准备定时器尚未设置 bReadyToPlay，应继续等待而不是重入 PlayerWaiting。");

        if (r.Path(pcClass) != "TGBioGame.TGBioPlayerController" ||
            r.Path(priClass) != "TGBioGame.TGBioPlayerReplicationInfo" ||
            state == 0 || r.Path(state) != "UTGame.TGGame.PendingMatch")
            throw new InvalidDataException("激活仅支持已核实的普通 BIO PC/PRI 与 PendingMatch。");

        if (preparationCommand != 0)
            AfdevBioBotPreparationInspector.RequirePreparing(process, world, preparationCommand);

        uint timer = r.FindField(state, "Timer", "UTGame.TGGame.PendingMatch.Timer");
        uint existingTimerEntry = r.U32(timer + 0xC8);

        if (r.Path(r.U32(timer + 0x34)) != "Core.Function" || r.U32(timer + 0xAC) != 0x20002 ||
            r.U16(timer + 0xB0) != 0 || r.U16(timer + 0xBE) != 0 || r.U16(timer + 0xC0) != 0xFFFF ||
            r.U32(timer + 0x58) != 2927 ||
            (existingTimerEntry != AfdevBioBotTimerHook.OriginalExec &&
                (timerEntry < 0x10000 || existingTimerEntry != timerEntry)))
            throw new InvalidDataException("PendingMatch.Timer 的精确脚本入口与审核映像不一致。");

        foreach (uint offset in new uint[] { 0x28, 0x34, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0xAC, 0xB0, 0xBC, 0xC0 })
            Proof(timer + offset);

        Put(0x130, timer);
        Put(0x134, AfdevBioBotTimerHook.OriginalExec);
        Put((int)AfdevBioBotTimerHook.EntrySlot, timerEntry);
        Put((int)AfdevBioBotActivationHook.BotCount, (uint)botCount);
        Put((int)AfdevBioBotActivationHook.HumanCount, (uint)fighters.Length);
        Put((int)AfdevBioBotActivationHook.FighterCapacity, (uint)fighterCapacity);
        Put((int)AfdevBioBotActivationHook.ObserverCount, (uint)(humans.Count - fighters.Length));
        Put((int)AfdevBioBotActivationHook.RegistrationCounter, registrationCounter);
        Put((int)AfdevBioBotActivationHook.BotIndex, (uint)botIndex);
        Put((int)AfdevBioBotActivationHook.PreparationPage, preparationCommand);
        foreach (var humanBit in new[]
        {
            (bot.Controller, "UTGame.TGPlayerController.bInitialProcessingComplete", AfdevBioBotActivationHook.HumanInitialProcessing),
            (bot.Pri, "UTGame.TGPlayerReplicationInfo.bLoadingComplete", AfdevBioBotActivationHook.HumanLoadingComplete),
            (bot.Controller, "UTGame.TGPlayerController.bWaitingLoadingProcessComplete", AfdevBioBotActivationHook.HumanWaitingLoading)
        })
        {
            var bit = Bit(humanBit.Item1, humanBit.Item2);

            Put((int)humanBit.Item3, bit.Offset);
            Put((int)humanBit.Item3 + 4, bit.Mask);
        }

        Put(0x40, world.World);
        Put(0x44, world.WorldInfo);
        Put(0x48, world.GameInfo);
        Put(0x4C, state);
        Put(0x50, bot.Controller);
        Put(0x54, bot.Pri);
        Put(0x58, pcClass);
        Put(0x5C, priClass);
        Put(0x60, Field(bot.Controller, "Engine.PlayerController.Player") - bot.Controller);
        Put(0x64, Field(bot.Controller, "Engine.Controller.PlayerReplicationInfo") - bot.Controller);
        Put(0x68, Field(bot.Controller, "Engine.Actor.WorldInfo") - bot.Controller);
        Put(0x6C, Field(bot.Pri, "Engine.Actor.Owner") - bot.Pri);
        Put(0x70, Field(bot.Pri, "Engine.Actor.WorldInfo") - bot.Pri);
        Put(0x74, Field(bot.Pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12) - bot.Pri);
        Put(0x78, Field(bot.Pri, "Engine.PlayerReplicationInfo.PlayerName", "StrProperty", 12) - bot.Pri);

        int bitSlot = 0x7C;

        foreach (string name in new[] { "bOnlySpectator", "bIsSpectator" })
        {
            var bit = Bit(bot.Pri, "Engine.PlayerReplicationInfo." + name);

            Put(bitSlot, bit.Offset);
            Put(bitSlot + 4, bit.Mask);
            bitSlot += 8;
        }

        bitSlot = 0x144;

        foreach (string name in new[] { "bAlwaysObserver", "bIsInternalDemoRecPlayer", "bGameMaster" })
        {
            var bit = Bit(bot.Pri, "UTGame.TGPlayerReplicationInfo." + name);

            Put(bitSlot, bit.Offset);
            Put(bitSlot + 4, bit.Mask);
            bitSlot += 8;
        }

        var waitingBit = Bit(bot.Pri, "Engine.PlayerReplicationInfo.bWaitingPlayer");
        var botBit = Bit(bot.Pri, "Engine.PlayerReplicationInfo.bBot");
        var warmupBit = Bit(world.GameInfo, "UTGame.UTGame.bWarmupRound");

        Put(0x15C, waitingBit.Offset);
        Put(0x160, waitingBit.Mask);
        Put(0x164, world.GameInfo + warmupBit.Offset);
        Put(0x168, warmupBit.Mask);
        Put(0x16C, 0);
        Put(0x170, Field(world.GameInfo, "UTGame.UTGame.MinNetPlayers", "IntProperty"));
        Put(0x174, world.NetDriver);
        Put(0x178, botBit.Offset);
        Put(0x17C, botBit.Mask);
        Put(0x180, Field(world.GameInfo, "Engine.GameInfo.NumSpectators", "IntProperty"));
        Put(0x184, Field(bot.Controller, "Engine.Controller.Pawn") - bot.Controller);

        Put(0x8C, Field(world.GameInfo, "Engine.GameInfo.NumPlayers", "IntProperty"));
        Put(0x90, Field(world.GameInfo, "Engine.GameInfo.NumBots", "IntProperty"));
        Put(0x94, Field(world.GameInfo, "Engine.GameInfo.NumTravellingPlayers", "IntProperty"));
        Put(0x98, Field(world.GameInfo, "Engine.GameInfo.MaxPlayers", "IntProperty"));

        uint teamField = Field(bot.Pri, "Engine.PlayerReplicationInfo.Team");
        uint team = r.U32(teamField);

        if (!r.IsA(team, "Engine.TeamInfo"))
            throw new InvalidDataException("准备名册中的 Bot 缺少原生 TeamInfo。");

        uint teamSize = Field(team, "Engine.TeamInfo.Size", "IntProperty");
        uint teamIndex = Field(team, "Engine.TeamInfo.TeamIndex", "IntProperty");

        uint size = r.U32(teamSize);

        if (size < (uint)botCount || size > (uint)fighterCapacity || r.U32(teamIndex) != 0 ||
            (preparationCommand == 0 && size != (uint)botCount))
            throw new InvalidDataException("准备阶段的原生人类 Team.Size 越过冻结 Bot 与真人容量。");

        Proof(teamIndex);
        Put((int)AfdevBioBotActivationHook.TeamSize, teamSize);
        Put((int)AfdevBioBotActivationHook.PriTeam, teamField);
        Put((int)AfdevBioBotActivationHook.Team, team);

        int readinessSlot = 0xC0;

        foreach (var requirement in new[]
        {
            (bot.Pri, "Engine.PlayerReplicationInfo.bBot", true),
            (bot.Pri, "UTGame.TGPlayerReplicationInfo.bLoadingComplete", true),
            (bot.Controller, "UTGame.TGPlayerController.bInitialProcessingComplete", true),
            (bot.Controller, "UTGame.TGPlayerController.bWaitingLoadingProcessComplete", false),
            (bot.Pri, "Engine.PlayerReplicationInfo.bWaitingPlayer", true),
            (bot.Pri, "Engine.PlayerReplicationInfo.bReadyToPlay", true),
            (world.GameInfo, "Engine.GameInfo.bWaitingToStartMatch", true)
        })
        {
            var bit = Bit(requirement.Item1, requirement.Item2);

            Put(readinessSlot, requirement.Item1 + bit.Offset);
            Put(readinessSlot + 4, bit.Mask);
            Put(readinessSlot + 8, requirement.Item3 ? bit.Mask : 0);
            readinessSlot += 12;
        }

        Put(0x120, waiting.State.Address);

        void Text(int offset, int lengthOffset, string value, int maximum)
        {
            if (value.Length == 0 || value.Length > maximum || value.Any(char.IsControl))
                throw new InvalidDataException("激活身份字符串超出冻结边界。");

            byte[] bytes = Encoding.Unicode.GetBytes(value + '\0');

            bytes.CopyTo(command, offset);
            Put(lengthOffset, (uint)value.Length + 1);
        }

        for (int index = 0; index < fighters.Length; index++)
        {
            AfdevBioBotHumanIdentity human = fighters[index];
            int offset = AfdevBioBotActivationHook.HumanTableOffset + index * AfdevBioBotActivationHook.HumanStride;

            human.SessionId.TryWriteBytes(command.AsSpan(offset, 16));
            Text(offset + 0x20, offset + 0x10, human.Uin.ToString(CultureInfo.InvariantCulture), 20);
            Text(offset + 0x60, offset + 0x14, human.Nickname, 64);
        }

        Text(0xE00, 0xA4, bot.Uin, 32);
        Text(0xE80, 0xA8, bot.Nickname, 64);
        Put(0xAC, r.U32(0x01FE22C0));
        Put(0xB8, r.U32(0x02063820));
        Proof(world.GameInfo + 0x34);

        QueryObject[] identities = r.IdentityClosure(world.World, world.WorldInfo, world.GameInfo,
            state, bot.Controller, bot.Pri, waiting.State.Address, timer, team).ToArray();

        if (identities.Length is 0 or > AfdevBioBotActivationHook.IdentityCapacity ||
            proofs.Count is 0 or > AfdevBioBotActivationHook.ProofCapacity)
            throw new InvalidDataException($"激活描述器超出固定预算：identities={identities.Length}/" +
                $"{AfdevBioBotActivationHook.IdentityCapacity}，proofs={proofs.Count}/" +
                $"{AfdevBioBotActivationHook.ProofCapacity}，证明结束=0x" +
                $"{AfdevBioBotActivationHook.ProofOffset + proofs.Count * 8:X}；尚未发布原生命令。");

        Put(0xB0, (uint)identities.Length);
        Put(0xB4, (uint)proofs.Count);

        for (int index = 0; index < identities.Length; index++)
        {
            QueryObject id = identities[index];
            uint[] values = [id.Address, id.Index, id.Outer, id.NameIndex, id.NameNumber, id.Class];

            for (int field = 0; field < values.Length; field++)
                Put(AfdevBioBotActivationHook.IdentityOffset + index * 24 + field * 4, values[field]);
        }

        int proofIndex = 0;

        foreach (var proof in proofs)
        {
            Put(AfdevBioBotActivationHook.ProofOffset + proofIndex * 8, proof.Key);
            Put(AfdevBioBotActivationHook.ProofOffset + 4 + proofIndex * 8, proof.Value);
            proofIndex++;
        }

        return command;
    }
}
