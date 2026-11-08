using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Checks automatic native PlayerWaiting state and readiness intent. Spawn's BeginState already sets
/// waiting flags/timers. Never repeat GotoState/BeginState or write Ready manually; await existing
/// DoForcedRespawn while not Ready.
/// </summary>
internal sealed class AfdevBioBotWaitingInspector(AfdevOwnedProcess process)
{
    public AfdevBioBotWaitingEvidence Capture(AfdevNativeEvidence world, uint controller)
    {
        var reader = new QueryInspector(process);
        var resources = new BioResourceInspector(process);

        resources.Prepare(world, controller);

        ResourceLoadingState loading = resources.CaptureLoadingState();
        uint pri = reader.U32(reader.FieldAddress(controller, "PlayerReplicationInfo",
            "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));
        uint frame = reader.U32(controller + 0x18);
        uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);

        if (state == 0 || reader.Path(state) != "UTGame.TGPlayerController.PlayerWaiting")
            throw new InvalidDataException("Bot 已离开原生自动 PlayerWaiting，禁止重新进入状态伪造准备完成。");

        reader.Identity(state);

        ResourceBool Bit(uint instance, string path)
        {
            uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, "BoolProperty", 4);
            uint field = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);
            uint mask = reader.U32(field + 0x84);
            uint word = reader.U32(address);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("准备状态布尔属性掩码无效。");

            return new ResourceBool(path, address, mask, word, (word & mask) != 0);
        }

        float Float(uint instance, string path)
        {
            uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, "FloatProperty", 4);
            float value = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(address, 4));

            if (!float.IsFinite(value))
                throw new InvalidDataException("准备状态时间条件不是有限数值。");

            return value;
        }

        ResourceBool waiting = Bit(pri, "Engine.PlayerReplicationInfo.bWaitingPlayer");
        ResourceBool ready = Bit(pri, "Engine.PlayerReplicationInfo.bReadyToPlay");
        ResourceBool gameWaiting = Bit(world.GameInfo, "Engine.GameInfo.bWaitingToStartMatch");
        ResourceBool warmup = Bit(world.GameInfo, "UTGame.UTGame.bWarmupRound");
        float worldTime = Float(world.WorldInfo, "Engine.WorldInfo.TimeSeconds");
        float waitDelay = Float(controller, "Engine.PlayerController.WaitDelay");
        ResourceTimer[] forcedRespawn = loading.Timers.Where(timer => timer.Name == "DoForcedRespawn").ToArray();

        if (!gameWaiting.Value || warmup.Value || !waiting.Value || forcedRespawn.Length != 1 ||
            forcedRespawn[0].NameNumber != 0 || forcedRespawn[0].Rate != 1 || forcedRespawn[0].Owner != controller)
            throw new InvalidDataException("原生 PlayerWaiting 的等待标志、倒计时或唯一准备定时器与已核实路径不一致。");

        return new AfdevBioBotWaitingEvidence(reader.Identity(controller), reader.Identity(pri),
            reader.Identity(state), waiting, ready, gameWaiting, warmup, worldTime, waitDelay, forcedRespawn[0]);
    }

    /// <summary>
    /// Describes the original method resolved from current state when research calls require it, without
    /// granting publication rights. The call gate must validate PlayerWaiting, bWaitingToStartMatch,
    /// nonwarmup state, and WaitDelay on the same game thread; otherwise the method may RestartPlayer.
    /// Normal preparation awaits the existing native timer.
    /// </summary>
    public ResourceFunction DescribeReadyRequest(AfdevNativeEvidence world, uint controller)
    {
        AfdevBioBotWaitingEvidence evidence = Capture(world, controller);

        if (evidence.WorldTime < evidence.WaitDelay)
            throw new InvalidOperationException("原生 WaitDelay 尚未到期，不能把无效果调用当作准备成功。");

        return new ResourceFunctionCatalog(process).Describe(controller, "TGBioGame.TGBioPlayerController",
            "UTGame.UTPlayerController.PlayerWaiting.ServerRestartPlayer", 0x2200C2, 0, 0, 0xFFFF, 261,
            "4f9a581fd637509d4f8c8ffd889ab393201d6237c865b541f2b3c3dd52cfb6c5", [],
            explicitSearchType: evidence.State.Address);
    }
}

internal sealed record AfdevBioBotWaitingEvidence(QueryObject Controller, QueryObject Pri, QueryObject State,
    ResourceBool Waiting, ResourceBool Ready, ResourceBool GameWaiting, ResourceBool Warmup,
    float WorldTime, float WaitDelay, ResourceTimer ForcedRespawn);
