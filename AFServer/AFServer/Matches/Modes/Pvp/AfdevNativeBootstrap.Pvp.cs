using System.Buffers.Binary;
using System.Text;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    private byte[] pvpTickCall = [];

    /// <summary>
    /// Synchronizes frozen rules/native bots before first PvP-world publication. Native PendingMatch still
    /// waits for human login.
    /// </summary>
    internal async Task ApplyPvpAsync(AfdevNativeEvidence world, NativeMatchDescriptor descriptor,
        CancellationToken token, AfdevBioBotRuntime? dormantBioRuntime = null)
    {
        if (!AfdevMapCatalog.SupportsPvp(world.ModeId, world.MapId)) return;

        Require(AfdevMapCatalog.MatchesPvp(world) && world.AttemptId == AttemptId &&
            world.ProcessId == Process.ProcessId && world.ProcessCreationTimeUtc == Process.CreationTimeUtc &&
            descriptor.ModeId == world.ModeId && descriptor.MapId == world.MapId &&
            descriptor.FighterCapacity == FighterCapacity, "PvP设置不属于当前冻结世界。");

        var reader = new QueryInspector(Process);

        pvpTickCall = dormantBioRuntime?.GetDormantTickCall(Process, world) ?? Convert.FromHexString("E80E290D00");

        var settings = AfdevPvpSettings.Read(descriptor);
        bool teamMode = AfdevMapCatalog.UsesTeamSeats(world.ModeId);
        var botOptions = world.ModeId switch
        {
            0x1001 => Validation.Options.BombBots,
            0x1004 => Validation.Options.IndividualBots,
            _ => Validation.Options.TeamBots
        };
        string prefix = world.ModeId switch { 0x1001 => "BM", 0x1004 => "IM", _ => "TM" };
        string addBot = settings.Bomb ? "UTGame.TGRoundMatch.AddBot" : "UTGame.TGGame.AddBot";
        uint game = world.GameInfo;
        uint gri = reader.U32(PvpField(game, "Engine.GameInfo.GameReplicationInfo"));

        Require(reader.Path(reader.Identity(gri).Class) == "UTGame.TG" + prefix + "GameReplicationInfo" &&
            reader.U32(PvpField(game, "Engine.GameInfo.NumPlayers")) == 0 &&
            reader.U32(PvpField(game, "Engine.GameInfo.NumBots")) == 0 && reader.U32(world.NetDriver + 0x48) == 0,
            "PvP发布前必须是本次空世界。");

        // Replace only the direct-connect preflight's team-read expression. Preserve length, all jump targets, and the original capacity check.
        if (teamMode)
        {
            await PatchPvpLoginCampAsync(world, "PreLogin", 691, 0x194, token).ConfigureAwait(false);
            await PatchPvpLoginCampAsync(world, "Login", 716, 0x150, token).ConfigureAwait(false);
        }

        uint robotField = reader.FindField(world.GameInfoClass, "bEnableRobot", "UTGame.UTGame.bEnableRobot");
        uint robotAddress = game + reader.U32(robotField + 0x60), robotMask = reader.U32(robotField + 0x84);

        Require(reader.Path(reader.U32(robotField + 0x34)) == "Core.BoolProperty" &&
            robotMask != 0 && (robotMask & (robotMask - 1)) == 0, "PvP 原生人机开关布局变化。");
        Process.SuspendMainThread();
        try
        {
            uint value = reader.U32(robotAddress);

            Process.WriteMemory(robotAddress, BitConverter.GetBytes(botOptions.Enabled ? value | robotMask : value & ~robotMask));
        }
        finally { Process.ResumeMainThread(); }

        uint settingsField = reader.FindField(world.GameInfoClass, "GameSettings", "UTGame.TGGame.GameSettings");
        uint settingsType = reader.U32(settingsField + 0x84);
        byte[] parameters = new byte[40];

        Require(reader.Path(settingsType) == "UTGame.TGGame.GameSpecificSettings", "PvP设置结构变化。");
        Put("ModeId", world.ModeId); Put("MapId", world.MapId); Put("SubModeId", descriptor.SubModeId);
        Put("Flags", descriptor.RoomFlags); Put("RuleType", (uint)settings.Rule);
        Put("TargetKills", settings.Bomb ? 0 : (uint)settings.GoalScore);
        Put("TargetRounds", settings.Bomb ? (uint)settings.GoalScore : 0);
        Put("TimeLimit", settings.Rule == 1 ? (uint)settings.Value : 0);
        Put("RespawnTime", (uint)settings.RespawnTime);
        // These boolean bits map native Flags definitions and synchronize with original lobby flags.
        foreach (string name in new[] { "bFriendDamgeEnabled", "bThrowWeaponEnabled", "bCanUseThrowWeapon",
            "bHeadShotMode", "bDisableArmor", "bDynamicBalanceEnabled", "bAutoBalanceEnabled" })
        {
            uint field = reader.FindField(settingsType, name, "UTGame.TGGame.GameSpecificSettings." + name);
            uint offset = reader.U32(field + 0x60), mask = reader.U32(field + 0x84);

            Require(reader.Path(reader.U32(field + 0x34)) == "Core.BoolProperty" && offset + 4 <= parameters.Length &&
                mask != 0 && (mask & (mask - 1)) == 0, "PvP设置布尔位布局变化：" + name);

            bool enabled = name switch
            {
                "bFriendDamgeEnabled" => (descriptor.RoomFlags & 0x10) != 0,
                "bThrowWeaponEnabled" => (descriptor.RoomFlags & 0x1000) != 0,
                "bCanUseThrowWeapon" => (descriptor.RoomFlags & 0x800) == 0,
                "bHeadShotMode" => (descriptor.RoomFlags & 0x100) != 0,
                "bDisableArmor" => (descriptor.RoomFlags & 0x200) != 0,
                "bDynamicBalanceEnabled" => (descriptor.RoomFlags & 0x10000) != 0,
                "bAutoBalanceEnabled" => (descriptor.RoomFlags & 0x100000) != 0,
                _ => false
            };

            if (enabled) BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan((int)offset),
                BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan((int)offset)) | mask);
        }

        await InvokePvpFunctionAsync(world, game, settings.Bomb ? "UTGame.TGBombMatch.UpdateGameSpecificSettings" :
            world.ModeId == 0x1004 ? "UTGame.TGIndMatch.UpdateGameSpecificSettings" : "UTGame.TGTeamMatch.UpdateGameSpecificSettings",
            new() { ["Settings"] = parameters }, token).ConfigureAwait(false);
        Require(reader.U32(PvpField(game, "Engine.GameInfo.GoalScore")) == settings.GoalScore &&
            reader.U32(PvpField(game, "Engine.GameInfo.TimeLimit")) == settings.TimeSeconds &&
            (settings.Bomb || reader.U32(PvpField(game, "UTGame.TGGame.RespawnTime")) == settings.RespawnTime),
            "PvP原生规则写入回读不一致。");

        int[] humans = teamMode
            ? [descriptor.Members.Count(member => !member.Observer && TeamDeathmatchSeats.Camp(member.SeatIndex) == 0),
                descriptor.Members.Count(member => !member.Observer && TeamDeathmatchSeats.Camp(member.SeatIndex) == 1)]
            : [descriptor.Members.Count(member => !member.Observer)];
        int botCount = 0;

        if (botOptions.Enabled)
        {
            if (!teamMode) SelectIndividualBotClass(world);

            // Native direct-connect PreLogin creates the weapon reference manager and loads defaults; AddBot alone skips this step.
            // Call no Login/PostLogin, create no human Controllers, and increment no human counts.
            var prepared = await InvokePvpFunctionAsync(world, game, teamMode ? "UTGame.TGTeamGame.PreLogin" : "UTGame.TGGame.PreLogin", new()
            {
                ["Options"] = PvpString("?Uin=0?Team=1?SpectatorOnly=0?Observe=0"),
                ["Address"] = PvpString("127.0.0.1")
            }, token).ConfigureAwait(false);
            uint errorOffset = PvpParameter(game, teamMode ? "UTGame.TGTeamGame.PreLogin" : "UTGame.TGGame.PreLogin", "ErrorMessage");

            Require(BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan((int)errorOffset + 4)) <= 1 &&
                reader.U32(PvpField(game, "UTGame.TGGame.MyRefInvManager")) >= 0x10000,
                "PvP原生装备预加载失败。");
            for (int camp = 0; camp < humans.Length; camp++)
            {
                int count = FighterCapacity / humans.Length - humans[camp];

                Require(count >= 0, "PvP某队真人超过冻结队伍容量。");
                for (int index = 0; index < count; index++)
                {
                    var added = await InvokePvpFunctionAsync(world, game, addBot, new()
                    {
                        ["bUseTeamIndex"] = BitConverter.GetBytes(teamMode ? 1 : 0), ["TeamIndex"] = BitConverter.GetBytes(camp)
                    }, token).ConfigureAwait(false);
                    uint bot = BinaryPrimitives.ReadUInt32LittleEndian(added.AsSpan((int)PvpParameter(game, addBot, "ReturnValue")));

                    log($"PVP_NATIVE_BOT mode=0x{world.ModeId:X4} class={(bot < 0x10000 ? "none" : reader.Path(reader.Identity(bot).Class))}");
                    Require(bot >= 0x10000 && reader.IsA(bot, settings.Bomb ? "UTGame.TGBMBot" : world.ModeId == 0x1004 ? "UTGame.TGBot" : "UTGame.TGTMBot"), "原生PvP 人机创建失败或类型变化。");

                    uint pri = reader.U32(PvpField(bot, "Engine.Controller.PlayerReplicationInfo"));
                    uint team = reader.U32(PvpField(pri, "Engine.PlayerReplicationInfo.Team"));

                    Require(reader.IsA(pri, "UTGame.TG" + prefix + "PlayerReplicationInfo") &&
                        (!teamMode || reader.U32(PvpField(team, "Engine.TeamInfo.TeamIndex")) == camp),
                        "原生PvP 人机的 PRI 或分队不一致。");
                    await InvokePvpFunctionAsync(world, bot, "UTGame.TGBot.SetBotAILevel", new()
                    {
                        ["nLevel"] = BitConverter.GetBytes((int)botOptions.Difficulty)
                    }, token).ConfigureAwait(false);
                    botCount++;
                }
            }
        }

        Require(reader.U32(PvpField(game, "Engine.GameInfo.NumBots")) == botCount &&
            reader.U32(PvpField(game, "Engine.GameInfo.NumPlayers")) == 0 && reader.U32(world.NetDriver + 0x48) == 0,
            "PvP 人机补位数量或发布前真人边界变化。");
        log($"PVP_SETTINGS_APPLIED rule={settings.Rule} value={settings.Value} respawn={settings.RespawnTime} " +
            $"mode=0x{world.ModeId:X4} humans={string.Join('/', humans)} native_bots={botCount} difficulty={botOptions.Difficulty}");

        void Put(string name, uint value)
        {
            uint field = reader.FindField(settingsType, name, "UTGame.TGGame.GameSpecificSettings." + name);
            uint offset = reader.U32(field + 0x60);

            Require(reader.Path(reader.U32(field + 0x34)) == "Core.IntProperty" && offset + 4 <= parameters.Length,
                "PvP设置字段布局变化：" + name);
            BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan((int)offset), value);
        }
    }

    private void SelectIndividualBotClass(AfdevNativeEvidence world)
    {
        var reader = new QueryInspector(Process);
        uint field = PvpField(world.GameInfo, "UTGame.UTGame.BotClass");

        // TGIndMatch retains UTGame's default UTBot, which lacks this TGame build's weapon/difficulty behavior.
        // Replace only this match instance's class reference with the existing TGBot, preserving default class objects and disk packages.
        Require(reader.Path(reader.U32(field)) == "UTGame.UTBot", "个人战原始 BotClass 不是已核验的 UTBot。");

        uint count = reader.U32(0x02053EAC), table = reader.U32(0x02053EA8), selected = 0;

        Require(count is > 0 and <= 1000000 && table >= 0x10000, "个人战类查找的 UObject 数组无效。");

        byte[] objects = Process.ReadMemory(table, checked((int)count * 4));

        for (int index = 0; index < count; index++)
        {
            uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(objects.AsSpan(index * 4));

            if (candidate < 0x10000 || reader.U32(candidate + 0x30) != 0 ||
                reader.Name(reader.U32(candidate + 0x2C)) != "TGBot" || reader.Path(candidate) != "UTGame.TGBot")
                continue;

            Require(selected == 0 && reader.Path(reader.Identity(candidate).Class) == "Core.Class",
                "个人战原生 TGBot 类身份无效或不唯一。");
            selected = candidate;
        }
        Require(selected != 0, "个人战原生 TGBot 类未加载。");
        Process.SuspendMainThread();
        try
        {
            Require(ReadUInt32(GWorld) == world.World && reader.U32(world.NetDriver + 0x48) == 0 &&
                reader.Path(reader.U32(field)) == "UTGame.UTBot", "个人战人机类设置前世界发生变化。");
            Process.WriteMemory(field, BitConverter.GetBytes(selected));
            // UTGame.RestartPlayer removes bots according to DesiredPlayerCount, whose native default is 1;
            // synchronize it with frozen capacity or native code will automatically destroy later-joining or respawning bots.
            Process.WriteMemory(PvpField(world.GameInfo, "UTGame.UTGame.DesiredPlayerCount"), BitConverter.GetBytes((int)FighterCapacity));
        }
        finally { Process.ResumeMainThread(); }
        log($"PVP_INDIVIDUAL_BOT_CLASS UTGame.UTBot -> UTGame.TGBot desired_players={FighterCapacity} instance_only=true");
    }

    private byte[] PvpString(string value)
    {
        byte[] text = Encoding.Unicode.GetBytes(value + "\0");
        uint data = Process.AllocateMemory(text.Length);

        Process.WriteMemory(data, text);

        return [.. BitConverter.GetBytes(data), .. BitConverter.GetBytes(value.Length + 1), .. BitConverter.GetBytes(value.Length + 1)];
    }

    private uint PvpField(uint instance, string path)
    {
        var reader = new QueryInspector(Process);
        uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);
        string kind = reader.Name(reader.U32(reader.U32(property + 0x34) + 0x2C));

        Require(kind is "IntProperty" or "ObjectProperty" or "ClassProperty", "PvP字段类型变化：" + path);

        return reader.FieldAddress(instance, path.Split('.')[^1], path, kind, 4);
    }

    private uint PvpFunction(uint target, string path)
    {
        var reader = new QueryInspector(Process);
        string ownerPath = path[..path.LastIndexOf('.')];
        uint owner = reader.U32(target + 0x34);

        // Bomb-mode TGRoundMatch overrides Login. Resolve by explicit owner when fixing/calling the parent,
        // rather than mistake the override for validated TGTeamGame bytecode.
        for (int depth = 0; owner != 0 && reader.Path(owner) != ownerPath && depth < 32; depth++)
            owner = reader.U32(owner + 0x48);

        Require(owner != 0 && reader.Path(owner) == ownerPath, "PvP 函数所有者不在当前对象继承链：" + path);

        uint function = reader.FindField(owner, path.Split('.')[^1], path);

        Require(reader.Path(reader.U32(function + 0x34)) == "Core.Function" && reader.U16(function + 0xB0) == 0 &&
            reader.U32(function + 0xC8) == 0x0048DB10 && reader.U32(function + 0x58) == reader.U32(function + 0x5C) &&
            reader.U32(reader.U32(target) + 0x120) == 0x0075A9F0, "PvP调用不是已核验的普通 Actor 脚本函数：" + path);

        return function;
    }

    private uint PvpParameter(uint target, string path, string name)
    {
        var reader = new QueryInspector(Process);
        uint function = PvpFunction(target, path);
        uint field = reader.FindField(function, name, path + "." + name);

        return reader.U32(field + 0x60);
    }

    private async Task<byte[]> InvokePvpFunctionAsync(AfdevNativeEvidence world, uint target, string path,
        Dictionary<string, byte[]> values, CancellationToken token)
    {
        var reader = new QueryInspector(Process);
        uint function = PvpFunction(target, path);
        uint size = reader.U16(function + 0xBE);
        byte[] parameters = new byte[Math.Max(size, 4)];

        Require(size <= 512, "PvP脚本参数超过有界范围。");
        foreach (var (name, value) in values)
        {
            uint offset = PvpParameter(target, path, name);

            Require(offset + value.Length <= size, "PvP脚本参数越界：" + name);
            value.CopyTo(parameters, (int)offset);
        }

        // Reuse the existing post-Tick wrapper for script calls after native Tick returns, avoiding interruption of loading or UObject construction.
        uint page = Process.AllocateMemory(0x3000);
        uint command = page + 0x2000, arguments = command + 0x100;
        byte[] original = Process.ReadMemory(TickHook.CallSite, 5);

        Require(original.AsSpan().SequenceEqual(pvpTickCall),
            "PvP准备阶段的游戏线程入口已有未知钩子。");
        Process.WriteMemory(arguments, parameters);

        var wrapper = TickHook.Build(page, page + 0x1000);
        uint previousTarget = unchecked(TickHook.CallSite + 5 + BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(1)));
        int tickDisplacement = wrapper.AsSpan().IndexOf(Convert.FromHexString("FF742408FF742408E8")) + 9;

        BinaryPrimitives.WriteUInt32LittleEndian(wrapper.AsSpan(tickDisplacement),
            unchecked(previousTarget - page - (uint)tickDisplacement - 4));

        var code = new X86Code().Bytes(wrapper.AsSpan(0, 0xBC).ToArray());
        var branches = new List<int>();

        Guard(command, 1); Guard(0x01FE22C0, Process.MainThreadId); Guard(0x01FE22C4, 1);
        Guard(page + 0x1014, Process.MainThreadId); Guard(GWorld, world.World);
        Guard(world.WorldInfo + 0x414, world.GameInfo);
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 }) Guard(address, 0);
        code.Store(command, 2).Hex("6A0068").UInt32(arguments).Byte(0x68).UInt32(function).Byte(0xB9).UInt32(target);
        code.Byte(0xB8).UInt32(0x0075A9F0).Hex("FFD0").Store(command, 3);

        int tail = code.ToArray().Length;

        code.Bytes(wrapper.AsSpan(0xBC).ToArray());

        byte[] program = code.ToArray();

        foreach (int branch in branches) BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(branch), tail - branch - 4);
        Process.WriteMemory(page, program);

        uint previous = Process.SuspendMainThread();

        try
        {
            Require(previous == 0 && Process.ReadMemory(TickHook.CallSite, 5).AsSpan().SequenceEqual(original),
                "安装PvP线程门前原生入口发生变化。");
            Process.WriteMemory(TickHook.CallSite, TickHook.CallPatch(page));
            Process.WriteMemory(command, BitConverter.GetBytes(1));
        }
        finally { Process.ResumeMainThread(); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            WaitingCondition = "pvp native post-tick script " + path;
            while (reader.U32(command) != 3)
                await WaitForProgressAsync(timeout.Token).ConfigureAwait(false);
            VerifyNoNativeFailure();

            return Process.ReadMemory(arguments, parameters.Length);
        }
        finally
        {
            if (Process.IsAlive)
            {
                Process.SuspendMainThread();
                try
                {
                    Require(Process.ReadMemory(TickHook.CallSite, 5).AsSpan().SequenceEqual(TickHook.CallPatch(page)),
                        "PvP线程门回收前入口发生变化。");
                    Process.WriteMemory(TickHook.CallSite, original);
                }
                finally { Process.ResumeMainThread(); }
            }
        }

        void Guard(uint address, uint expected)
        {
            code.Hex("813D").UInt32(address).UInt32(expected).Hex("0F85");
            branches.Add(code.ToArray().Length); code.UInt32(0);
        }
    }

    private async Task PatchPvpLoginCampAsync(AfdevNativeEvidence world, string name, uint length, uint offset,
        CancellationToken token)
    {
        var reader = new QueryInspector(Process);
        string path = "UTGame.TGTeamGame." + name;
        uint function = PvpFunction(world.GameInfo, path);
        uint script = reader.U32(function + 0x54);
        var expected = new List<byte> { 0x38, 0x3A, 0x35 };
        uint structure = reader.FindField(world.GameInfoClass, "OnlineDs", "UTGame.TGGame.OnlineDs");
        uint onlineClass = reader.U32(structure + 0x84);
        uint info = reader.FindField(onlineClass, "DsPlayerInfo", "UTGame.TGOnlineDS.DsPlayerInfo");
        uint hud = reader.FindField(info, "HudCamp", "UTGame.TGOnlineDS.DsPlayerInfo.HudCamp");
        uint local = reader.FindField(function, "PlayerInfo", path + ".PlayerInfo");
        uint options = reader.FindField(function, "Options", path + ".Options");
        uint getIntOption = reader.FindField(world.GameInfoClass, "GetIntOption", "Engine.GameInfo.GetIntOption");
        uint gameMaster = reader.FindField(info, "bGameMaster", "UTGame.TGOnlineDS.DsPlayerInfo.bGameMaster");

        Reference(expected, hud); Reference(expected, info); expected.AddRange(new byte[] { 0, 0, 0 }); Reference(expected, local);

        var replacement = new List<byte> { 0x1B };

        replacement.AddRange(Process.ReadMemory(getIntOption + 0x2C, 8));
        replacement.Add(0); Reference(replacement, options);
        replacement.AddRange(Encoding.ASCII.GetBytes("\x1FTeam\0"));
        replacement.Add(0x1D); replacement.AddRange(BitConverter.GetBytes(255)); replacement.Add(0x16);

        // Observers consume no team capacity. Observe comes from frozen relay identity and cannot be elevated by clients; humans still pass native team-count checks.
        var observerExpected = new List<byte> { 0x2D, 0x35 };
        var observerReplacement = new List<byte> { 0x1B };
        uint observerOffset = name == "PreLogin" ? 0x16Cu : 0x128u;

        Reference(observerExpected, gameMaster); Reference(observerExpected, info);
        observerExpected.AddRange(new byte[] { 0, 0, 0 }); Reference(observerExpected, local);
        observerReplacement.AddRange(Process.ReadMemory(getIntOption + 0x2C, 8));
        observerReplacement.Add(0); Reference(observerReplacement, options);
        observerReplacement.AddRange(Encoding.ASCII.GetBytes("\x1FObserve\0"));
        observerReplacement.AddRange(new byte[] { 0x25, 0x16 });
        Require(reader.U32(function + 0x58) == length && expected.Count == 30 && replacement.Count == 30 &&
            observerExpected.Count == 29 && observerReplacement.Count == 29 &&
            Process.ReadMemory(script + offset, expected.Count).AsSpan().SequenceEqual(expected.ToArray()) &&
            Process.ReadMemory(script + observerOffset, 29).AsSpan().SequenceEqual(observerExpected.ToArray()),
            "PvP直连阵营表达式与已核验原始字节码不同。");

        uint previous = Process.SuspendMainThread();

        try
        {
            Require(previous == 0 && ReadUInt32(GWorld) == world.World && reader.U32(world.NetDriver + 0x48) == 0 &&
                Process.ReadMemory(script + offset, expected.Count).AsSpan().SequenceEqual(expected.ToArray()) &&
                Process.ReadMemory(script + observerOffset, 29).AsSpan().SequenceEqual(observerExpected.ToArray()),
                "PvP阵营修复安装前世界或脚本变化。");
            Install(script + offset, replacement.ToArray());
            Install(script + observerOffset, observerReplacement.ToArray());
        }
        finally { Process.ResumeMainThread(); }
        log($"TEAM_DIRECT_CAMP_PATCH function={path} bytes={replacement.Count} jumps_unchanged=true");
        await Task.CompletedTask;

        static void Reference(List<byte> bytes, uint pointer)
        {
            bytes.AddRange(BitConverter.GetBytes(pointer)); bytes.AddRange(new byte[4]);
        }
    }
}
