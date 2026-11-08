using System.Buffers.Binary;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    /// <summary>
    /// Synchronizes frozen settings after first PVE-world readiness and before admission. Writes only state
    /// corresponding to native UpdateGameSpecificSettings, without reinitializing GRI or driving level
    /// scripts. All writes share one game-thread call. Lobby snapshots preserve original IDs/Elite flags;
    /// this boundary alone maps to three native tiers so levels and GRI agree.
    /// </summary>
    internal async Task ApplyPveSettingsAsync(AfdevNativeEvidence world, uint subModeId, uint roomFlags,
        CancellationToken cancellationToken)
    {
        if (world.ModeId is not (0x2001 or 0x2002 or 0x2003 or 0x2005)) return;

        Require(AfdevMapCatalog.MatchesPve(world) &&
            world.AttemptId == AttemptId && world.ProcessId == Process.ProcessId &&
            world.ProcessCreationTimeUtc == Process.CreationTimeUtc, "PVE 设置必须属于本次已接入的精确原生世界。");

        var reader = new QueryInspector(Process);
        uint settings = reader.FieldAddress(world.GameInfo, "GameSettings", "UTGame.TGGame.GameSettings", "StructProperty", 40);
        uint property = reader.FindField(world.GameInfoClass, "GameSettings", "UTGame.TGGame.GameSettings");
        uint structure = reader.U32(property + 0x84);
        uint griAddress = reader.FieldAddress(world.GameInfo, "GameReplicationInfo", "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4);
        uint gri = reader.U32(griAddress);
        uint worldFlags = reader.FieldAddress(world.WorldInfo, "GameSettingFlags", "Engine.WorldInfo.GameSettingFlags", "IntProperty", 4);
        uint difficultyAddress = reader.FieldAddress(gri, "Difficulty", "PVEGame.PVEGameReplicationInfo.Difficulty", "ByteProperty", 1);
        var gameIdentity = reader.Identity(world.GameInfo);
        var griIdentity = reader.Identity(gri);
        byte difficulty = world.ModeId switch
        {
            0x2001 => AfdevSurvivalSettings.Difficulty(subModeId, roomFlags),
            0x2002 => AfdevDefenseSettings.Difficulty(subModeId, roomFlags),
            0x2003 => AfdevEncirclementSettings.Difficulty(subModeId, roomFlags),
            _ => AfdevTowerDefenseSettings.Difficulty(subModeId, roomFlags)
        };
        uint nativeSubModeId = AfdevPveDifficulty.NativeSubMode(difficulty);
        string griClass = (world.ModeId, world.MapId) switch
        {
            (0x2001, _) => "PVEGame.TGSVGameReplicationInfo",
            (0x2003, _) => "LDGame.TGLDGameReplicationInfo",
            (0x2005, _) => "ATDGame.ATDGameGameReplicationInfo",
            (0x2002, 0x005F) => "TGIFGame.TGIF2GameReplicationInfo",
            _ => "PVEGame.TGIFGameReplicationInfo"
        };

        Require(reader.Path(structure) == "UTGame.TGGame.GameSpecificSettings" &&
            reader.Path(griIdentity.Class) == griClass, "PVE 设置结构或 GRI 类型与已核验包不一致。");

        var writes = new List<(uint Address, uint Value)>
        {
            (Setting("ModeId"), world.ModeId), (Setting("MapId"), world.MapId),
            (Setting("SubModeId"), nativeSubModeId), (Setting("Flags"), roomFlags), (worldFlags, roomFlags)
        };
        var code = new X86Code();
        var failures = new List<int>();

        Guard(GWorld, world.World);
        Guard(world.WorldInfo + 0x414, world.GameInfo);
        Guard(griAddress, gri);
        Guard(world.NetDriver + 0x48, 0); // No native ClientConnections should exist before endpoint publication.

        foreach (var write in writes) code.Store(write.Address, write.Value);
        code.Hex("C605").UInt32(difficultyAddress).Byte(difficulty);
        code.Byte(0xB8).UInt32(1).Hex("EB02");

        int failureOffset = code.ToArray().Length;

        code.Hex("31C0");

        byte[] program = code.ToArray();

        foreach (int branch in failures)
            BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(branch), failureOffset - branch - 4);

        WaitingCondition = "pve frozen settings on game thread";
        Require(await InvokeMainThreadAsync(program, cancellationToken).ConfigureAwait(false) == 1,
            "PVE 设置写入前世界、GRI 或准入状态发生变化，拒绝发布本局。");
        Require(reader.Identity(world.GameInfo) == gameIdentity && reader.Identity(gri) == griIdentity &&
            writes.All(write => reader.U32(write.Address) == write.Value) &&
            Process.ReadMemory(difficultyAddress, 1)[0] == difficulty, "PVE 冻结设置写入后回读不一致。");

        if (world.ModeId == 0x2003)
        {
            uint previous = Process.SuspendMainThread();

            try
            {
                Require(previous == 0, "围剿战传送入口安装遇到已挂起的主线程。");
                AfdevEncirclementTeleportPatch.Install(Process, world);
            }
            finally { Process.ResumeMainThread(); }

            log("LD_TELEPORT_OWNER_SYNC_READY native_success_branch=true reliable_client_location=true");
        }

        log($"PVE_SETTINGS_APPLIED mode=0x{world.ModeId:X4} map=0x{world.MapId:X4} " +
            $"submode=0x{subModeId:X4} native_submode=0x{nativeSubModeId:X4} flags=0x{roomFlags:X8} difficulty={difficulty}");

        uint Setting(string name)
        {
            uint field = reader.FindField(structure, name, "UTGame.TGGame.GameSpecificSettings." + name);
            uint offset = reader.U32(field + 0x60);

            Require(reader.Path(reader.U32(field + 0x34)) == "Core.IntProperty" &&
                reader.U32(field + 0x40) == 1 && reader.U32(field + 0x44) == 4 && offset + 4 <= 40,
                "PVE GameSpecificSettings 字段类型或范围不一致：" + name);

            return settings + offset;
        }

        void Guard(uint address, uint expected)
        {
            code.Hex("813D").UInt32(address).UInt32(expected).Hex("0F85");
            failures.Add(code.ToArray().Length);
            code.UInt32(0);
        }
    }
}
