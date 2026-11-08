using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Team revival choice before failure enters native EndGame. Checkpoints, candidates, and deduction
/// ordinals belong only to this exclusive world. The control plane prepares scripts during the synchronous
/// mailbox wait; restoration, destruction, and Kismet activation still execute on the original game thread.
/// </summary>
internal sealed class AfdevPveRoundRetryRuntime
{
    internal const uint CommandBase = 0x4E5A0010;
    private const string Prepare = "UTGame.TGGame.StoreCheckPoint";
    private const string Restore = "UTGame.TGGame.RestoreCheckPoint";
    private readonly AfdevOwnedProcess process;
    private readonly AfdevNativeEvidence world;
    private readonly QueryInspector reader;
    private readonly AfdevPveRetryProgram program;
    private readonly Action<string> log;
    private readonly uint online;
    private readonly uint gri;
    private readonly Dictionary<string, (byte[] Original, AfdevPveRetryScripts.Layout Layout)> originals = [];
    private readonly Dictionary<(uint Type, string Path), bool> types = [];
    private readonly HashSet<ulong> declined = [];
    private Checkpoint? checkpoint;
    private Participant[] participants = [];
    private int pendingReason = -1;
    private bool restoring;
    private bool finalFailure;
    private uint failureAction;
    private uint debitRequest;
    private uint debitSequence;
    internal uint Page { get; }

    private sealed record Participant(ulong Uin, uint Controller, uint Pri);
    private sealed record SavedValue(uint Target, string Path, byte[] Value, bool Boolean);
    private sealed record RoundSettings(int Number, uint Time, int SpawnLimit, int ExistLimit);
    private sealed record Checkpoint(int Kind, uint Entry, uint Scope, int PreviousRound,
        SavedValue[] Values, Dictionary<ulong, byte[]> Positions, RoundSettings? Round);

    internal AfdevPveRoundRetryRuntime(AfdevOwnedProcess process, AfdevNativeEvidence world, uint online, Action<string> log)
    {
        this.process = process;
        this.world = world;
        this.online = online;
        this.log = log;
        reader = new(process);
        gri = reader.U32(Address(world.GameInfo, "Engine.GameInfo.GameReplicationInfo", "ObjectProperty", 4));
        program = new(process);
        Page = process.AllocateMemory(4096);
        program.Objects();

        foreach (var layout in AfdevPveRetryScripts.All)
        {
            bool relevant = layout.Path.StartsWith("PVEGame.", StringComparison.Ordinal) ||
                layout.Path.StartsWith("UTGame.", StringComparison.Ordinal) ||
                (world.ModeId == 0x2005 && layout.Path.StartsWith("ATDGame.", StringComparison.Ordinal)) ||
                (world.MapId == 0x005F && layout.Path.StartsWith("TGIFGame.", StringComparison.Ordinal)) ||
                (world.ModeId == 0x2003 && layout.Path.StartsWith("LDGame.", StringComparison.Ordinal));

            if (relevant) originals.Add(layout.Path, (program.Verify(layout), layout));
        }
    }

    /// <summary>
    /// The caller suspends the main thread at first no-human world binding and installs this alongside the
    /// existing coin entry point.
    /// </summary>
    internal void Install()
    {
        if (reader.U32(AfdevNativeBootstrap.GWorld) != world.World || reader.U32(world.NetDriver + 0x48) != 0)
            throw new InvalidDataException("PVE 重试安装的世界或准入边界发生变化。");

        foreach (var (path, item) in originals)
        {
            if (path is Prepare or Restore or "PVEGame.PVEGame.NotifyPlayerUsedReviveCoin") continue;

            var prefix = new AfdevPveRetryProgram.Code();
            byte[] Gateway(int command, byte[] text, byte[] arg) => program.Gateway(online, unchecked((int)CommandBase) + command, text, arg);
            bool controller = path.Contains(".PVEPlayerController.", StringComparison.Ordinal);

            if (path == "PVEGame.PVEGame.Logout")
            {
                byte[] exiting = program.Variable(path + ".Exiting", true);
                byte[] pri = program.Context(exiting, program.Variable("Engine.Controller.PlayerReplicationInfo"),
                    program.Find("Engine.Controller.PlayerReplicationInfo"));
                byte[] uin = program.Context(pri, program.Variable("UTGame.TGPlayerReplicationInfo.Uin"),
                    program.Find("UTGame.TGPlayerReplicationInfo.Uin"));

                // Disconnect during the wait means that player declines revival. Release the pause when the last coin-holding player leaves.
                prefix.Unless("original", AfdevPveRetryProgram.Positive(Gateway(2, program.Text(""), program.Integer(0))));
                prefix.Unless("original", AfdevPveRetryProgram.Positive(Gateway(4, uin, program.Integer(0))));
                prefix.Emit(program.Call(world.GameInfo, Prepare));
            }
            else if (controller)
            {
                byte[] pri = program.Variable("Engine.Controller.PlayerReplicationInfo");
                byte[] uin = program.Context(pri, program.Variable("UTGame.TGPlayerReplicationInfo.Uin"), program.Find("UTGame.TGPlayerReplicationInfo.Uin"));

                prefix.Unless("original", AfdevPveRetryProgram.Positive(Gateway(2, uin, program.Integer(0))));

                bool use = path.EndsWith("ServerUseReviveCoin", StringComparison.Ordinal);

                prefix.Unless("rejected", AfdevPveRetryProgram.Positive(Gateway(use ? 3 : 4, uin, program.Integer(0))));
                if (use) prefix.Emit(program.Call(world.GameInfo, "PVEGame.PVEGame.NotifyPlayerUsedReviveCoin", [0x17]));
                prefix.Emit(program.Call(world.GameInfo, path.EndsWith("ServerUseReviveCoin", StringComparison.Ordinal) ? Restore : Prepare));
                prefix.Emit([4, 0x0B]);
                prefix.Label("rejected");
                if (use)
                {
                    prefix.Emit([0x0F, .. program.Context(pri, program.Variable("PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount"), program.Find("PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount")),
                        .. program.Gateway(online, 14, uin, program.Integer(0))]);
                    prefix.Emit(program.Call(world.GameInfo, Prepare));
                }

                prefix.Emit([4, 0x0B]);
            }
            else if (path.EndsWith("EndGameReason", StringComparison.Ordinal) || path == "PVEGame.PVESeqAct_EndGame.Activated")
            {
                byte[] reason = path.EndsWith("EndGameReason", StringComparison.Ordinal)
                    ? program.Variable(path + ".Reason", true)
                    : program.Variable("PVEGame.PVESeqAct_EndGame.EndGameReason");

                prefix.Unless("original", AfdevPveRetryProgram.Positive(Gateway(1,
                    program.Final("Core.Object.PathName", [0x17]), [0x38, 0x3A, .. reason])));
                prefix.Emit(program.Call(world.GameInfo, Prepare), [4, 0x0B]);
            }
            else
            {
                int kind = path.StartsWith("ATDGame.", StringComparison.Ordinal) ? 1 :
                    path.Contains("SetKillBigMonsterTime", StringComparison.Ordinal) ? 2 :
                    path.Contains("TGIF2", StringComparison.Ordinal) ? 4 : path.StartsWith("LDGame.", StringComparison.Ordinal) ? 5 : 3;

                prefix.Emit(Gateway(0, program.Final("Core.Object.PathName", [0x17]), program.Integer(kind)));
            }

            prefix.Label("original");

            byte[] head = prefix.Finish(false), tail = item.Original.ToArray();

            foreach (int offset in item.Layout.Jumps)
                BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(offset),
                    checked((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset)) + head.Length)));

            program.Install(path, [.. head, .. tail]);
        }
    }

    internal async Task ServiceAsync(MatchStartSnapshot snapshot, MatchStartMember[] members, IPveReviveCoinStore store,
        uint coinPage, CancellationToken token)
    {
        uint request = Read(Page);

        if (request == Read(Page + 4)) return;

        program.BeginRead();
        types.Clear();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int command = (int)Read(Page + 8), argument = (int)Read(Page + 12);
        string text = ReadString(Read(Page + 16));

        // UE PathName uses ':' between package and child objects; the reflection catalog uses '.'.
        if (command is 0 or 1) text = text.Replace(':', '.');

        int result = 0;

        if (command is 0 or 1) log($"PVE_RETRY_COMMAND command={command} argument={argument} source={(command == 0 ? text : "failure")}");

        switch (command)
        {
            case 0:
                Capture(text, argument, members);
                break;
            case 1:
                result = BeginFailure(argument, text, members);
                break;
            case 2:
                result = pendingReason >= 0 && !restoring ? 1 : 0;
                break;
            case 3:
                if (pendingReason >= 0 && !restoring && ulong.TryParse(text, out ulong uin) && !declined.Contains(uin))
                {
                    int index = Array.FindIndex(members, member => member.Profile.Uin == uin);

                    if (index >= 0 && Participants(members).Any(player => player.Uin == uin))
                    {
                        // Players may leave during the wait. Rebuild the recovery plan from exact still-online objects before deducting coins.
                        participants = Participants(members);
                        BuildRestore(checkpoint!);

                        uint slot = coinPage + (uint)(index * AfdevPveReviveCoinHook.SlotSize);

                        // Shares each human's monotonic receipt ordinal with in-place revival. Transient reentry into this request keeps the same ordinal.
                        if (debitRequest != request)
                        {
                            debitRequest = request;
                            debitSequence = checked(Read(slot + AfdevPveReviveCoinHook.Request) + 1);
                            Write(slot + AfdevPveReviveCoinHook.Operation, 1);
                            Write(slot + AfdevPveReviveCoinHook.Request, debitSequence);
                        }

                        int remaining = await store.ConsumeAsync(snapshot, members[index], debitSequence, token).ConfigureAwait(false);

                        Write(slot + 4, (uint)Math.Max(remaining, 0));
                        Write(slot + AfdevPveReviveCoinHook.Result, unchecked((uint)remaining));
                        Write(slot + AfdevPveReviveCoinHook.Completed, debitSequence);

                        if (remaining >= 0)
                        {
                            program.Install(Prepare, [4, 0x0B, 0x53]);
                            restoring = true;
                            result = 1;
                            log($"PVE_ROUND_RETRY_COMMITTED allocation={snapshot.AllocationId:N} reason={pendingReason} sequence={debitSequence}");
                        }
                        else
                        {
                            declined.Add(uin);
                            CompleteIfDeclined(members);
                        }
                    }
                }
                break;
            case 4:
                if (pendingReason >= 0 && !restoring && ulong.TryParse(text, out ulong declinedUin) &&
                    Participants(members).Any(player => player.Uin == declinedUin))
                {
                    declined.Add(declinedUin);
                    CompleteIfDeclined(members);
                    result = 1;
                }
                break;
            case 5:
                if (argument == 0)
                {
                    ResetImpulses(checkpoint!);

                    break;
                }

                pendingReason = -1;
                restoring = false;
                finalFailure = false;
                log($"PVE_ROUND_RETRY_RESUMED allocation={snapshot.AllocationId:N} kind={checkpoint?.Kind}");
                break;
            default:
                throw new InvalidDataException("PVE 重试内部命令超界。");
        }

        Write(Page + 24, (uint)result);
        Write(Page + 4, request);
        if (command is 0 or 1 or 3) log($"PVE_RETRY_COMMAND_COMPLETED command={command} result={result} elapsed_ms={timer.ElapsedMilliseconds}");
    }

    private bool CompleteIfDeclined(MatchStartMember[] members)
    {
        participants = Participants(members);
        if (participants.Any(player => !declined.Contains(player.Uin) &&
            Int(player.Pri, "PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount") > 0))
        {
            BuildWaiting();

            return false;
        }

        var code = ResumePlayers();

        code.Emit(failureAction != 0
            ? program.Call(failureAction, "PVEGame.PVESeqAct_EndGame.Activated")
            : program.Call(world.GameInfo, ActualEndGame(), [0x24, (byte)pendingReason]));
        program.Install(Prepare, code.Finish());
        finalFailure = true;
        pendingReason = -1;

        return true;
    }

    private int BeginFailure(int reason, string source, MatchStartMember[] members)
    {
        if (finalFailure) return 0;
        if (reason == 1 && pendingReason >= 0)
        {
            uint suspend = Address(gri, "PVEGame.PVEGameReplicationInfo.bSuspendGame", "BoolProperty", 4);

            // Native Logout ends normally when the last human disconnects. An offline PRI must not retain the pause.
            Write(Address(world.WorldInfo, "Engine.WorldInfo.Pauser", "ObjectProperty", 4), 0);
            Write(suspend, Read(suspend) & ~reader.U32(program.Find("PVEGame.PVEGameReplicationInfo.bSuspendGame") + 0x84));
            pendingReason = -1;

            return 0;
        }
        if (pendingReason >= 0) return 1;
        if (checkpoint is null || !((reason == 11 && checkpoint.Kind == 1) ||
            (reason == 3 && checkpoint.Kind == 2) || (reason == 6 && checkpoint.Kind == 3) ||
            (reason == 14 && checkpoint.Kind == 4) || (reason == 10 && checkpoint.Kind == 5))) return 0;

        participants = Participants(members);

        if (!participants.Any(player => Int(player.Pri, "PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount") > 0)) return 0;
        if (reader.U32(Address(world.WorldInfo, "Engine.WorldInfo.Pauser", "ObjectProperty", 4)) != 0)
            throw new InvalidDataException("PVE 回合失败前已有未知暂停所有者。");

        declined.Clear();

        uint caller = program.Find(source);

        failureAction = IsA(caller, "PVEGame.PVESeqAct_EndGame") ? caller : 0;

        // Validate every reflection, object, and sequence boundary in the recovery plan before promising a deduction.
        ResetImpulses(checkpoint, false);
        BuildRestore(checkpoint);
        BuildWaiting();
        pendingReason = reason;
        log($"PVE_ROUND_RETRY_PENDING reason={reason} kind={checkpoint.Kind} entry={program.Path(checkpoint.Entry)} candidates={participants.Length}");

        return 1;
    }

    private void BuildWaiting()
    {
        var eligible = participants.Where(player => !declined.Contains(player.Uin) &&
            Int(player.Pri, "PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount") > 0).ToArray();
        var prepare = new AfdevPveRetryProgram.Code();

        if (eligible.Length == 0) throw new InvalidDataException("PVE 重试等待缺少持币参战者。");

        prepare.Emit(program.Set(gri, "PVEGame.PVEGameReplicationInfo.bSuspendGame", [0x27], true));
        prepare.Emit(program.Set(world.WorldInfo, "Engine.WorldInfo.Pauser", program.Object(eligible[0].Pri)));
        foreach (var player in participants)
        {
            bool waiting = eligible.Any(candidate => candidate.Uin == player.Uin);

            prepare.Emit(program.Set(player.Controller, "PVEGame.PVEPlayerController.bGiveupRevive", [waiting ? (byte)0x28 : (byte)0x27], true));
            prepare.Emit(program.Call(player.Controller, waiting
                ? "PVEGame.PVEPlayerController.NotifyClientPlayerDied"
                : "PVEGame.PVEPlayerController.NotifyClientPlayerRespawned"));
        }

        program.Install(Prepare, prepare.Finish());
    }

    private void Capture(string path, int kind, MatchStartMember[] members)
    {
        if (restoring || pendingReason >= 0 || !path.Contains(".TheWorld.PersistentLevel.", StringComparison.Ordinal)) return;
        if (kind == 5 && !path.Contains(".StartBoss.", StringComparison.Ordinal)) return;

        if (kind == 4)
        {
            // IF2 Boss sublevels load progressively. Attach the entry to loaded Game and resolve the unique actual level node on entering the Boss stage.
            uint[] nodes = program.Objects().Where(item => IsA(item, "TGIFGame.TGIF2SeqAct_BeginBoss") &&
                program.Path(item).StartsWith("Level3_Script.TheWorld.PersistentLevel.Main_Sequence.Boss_Initialize.", StringComparison.Ordinal)).ToArray();

            if (nodes.Length != 1) throw new InvalidDataException($"IF2 Boss 检查点不是唯一关卡节点：{nodes.Length}。");

            path = program.Path(nodes[0]);
        }

        uint node = program.Find(path);
        uint scope = reader.U32(Address(node, "Engine.SequenceObject.ParentSequence", "ObjectProperty", 4));
        uint entry = kind == 1 ? node : scope;

        if (kind == 4) scope = reader.U32(Address(scope, "Engine.SequenceObject.ParentSequence", "ObjectProperty", 4));

        int round = kind == 1 ? Int(reader.U32(Address(gri, "ATDGame.ATDGameGameReplicationInfo.EnemySpawnerManager", "ObjectProperty", 4)),
            "ATDGame.ATDGame_EnemyWaveManager.mCurrentRoundIndex") : 0;
        var values = new List<SavedValue>();
        var positions = new Dictionary<ulong, byte[]>();
        string scopePath = program.Path(scope) + ".";
        RoundSettings? settings = kind == 5 ? new(Int(node, "LDGame.TGLDSeqAct_SetGameInfo.CurrentRound"),
            Read(Address(node, "LDGame.TGLDSeqAct_SetGameInfo.RoundTime", "FloatProperty", 4)),
            Int(node, "LDGame.TGLDSeqAct_SetGameInfo.RoundSpawnLimit"), Int(node, "LDGame.TGLDSeqAct_SetGameInfo.RoundExistLimit")) : null;

        foreach (uint item in program.Objects())
        {
            if (!IsA(item, "Engine.SequenceObject") || !program.Path(item).StartsWith(scopePath, StringComparison.Ordinal)) continue;

            Save(item, "Engine.SeqAct_Gate.bOpen", true);
            Save(item, "Engine.SeqAct_ActorFactory.bEnabled", true);
            Save(item, "Engine.SeqAct_ActorFactory.LastSpawnIdx", false);
            // Sequence.Reset ignores variables. Restore only sequence scalars to avoid retaining destroyed-Actor references.
            Save(item, "Engine.SeqVar_Bool.bValue", false);
            Save(item, "Engine.SeqVar_Int.IntValue", false);
            Save(item, "Engine.SeqVar_Float.FloatValue", false, true);
        }
        foreach (var player in Participants(members))
        {
            uint pawn = reader.U32(Address(player.Controller, "Engine.Controller.Pawn", "ObjectProperty", 4));

            if (pawn != 0) positions[player.Uin] = process.ReadMemory(Address(pawn, "Engine.Actor.Location", "StructProperty", 12), 12);
        }

        checkpoint = new(kind, entry, scope, round, values.ToArray(), positions, settings);
        log($"PVE_ROUND_CHECKPOINT kind={kind} entry={program.Path(entry)} previous_round={round}");

        void Save(uint target, string field, bool boolean, bool floating = false)
        {
            if (!IsA(target, field[..field.LastIndexOf('.')])) return;

            uint property = program.Find(field);
            uint raw = reader.U32(target + reader.U32(property + 0x60));

            byte[] value = boolean ? [(raw & reader.U32(property + 0x84)) != 0 ? (byte)0x27 : (byte)0x28] :
                floating ? program.Float(raw) : program.Integer((int)raw);

            values.Add(new(target, field, value, boolean));
        }
    }

    private void BuildRestore(Checkpoint saved)
    {
        var code = ResumePlayers();
        string scope = program.Path(saved.Scope);
        var objects = program.Objects();

        // Use native Destroy for engine cleanup, then clear events it may enqueue into the current sequence.
        foreach (uint item in objects.Where(item => IsA(item, "PVEGame.PVEAIPawn") && IsWorldActor(item)))
            code.Emit(program.Call(item, "Engine.Actor.Destroy"));
        foreach (uint item in objects.Where(item => IsA(item, "PVEGame.PVEAIController") && IsWorldActor(item)))
            code.Emit(program.Call(item, "Engine.Actor.Destroy"));
        foreach (uint item in objects.Where(item => IsA(item, "Engine.Projectile") && IsWorldActor(item)))
            code.Emit(program.Call(item, "Engine.Actor.Destroy"));

        foreach (uint item in objects.Where(item => IsA(item, "Engine.SeqAct_Interp") &&
            program.Path(item).StartsWith(scope + ".", StringComparison.Ordinal)))
            code.Emit(program.Call(item, "Engine.SeqAct_Interp.Stop"));

        code.Emit(program.Call(saved.Scope, "Engine.Sequence.Reset"));

        foreach (uint item in objects)
        {
            if (!IsA(item, "Engine.SequenceOp")) continue;

            string path = program.Path(item);

            if (path != scope && !path.StartsWith(scope + ".", StringComparison.Ordinal)) continue;

            if (IsA(item, "Engine.Sequence"))
                foreach (string list in new[] { "ActiveSequenceOps", "DelayedActivatedOps", "QueuedActivations" })
                    code.Emit(program.ClearArray(item, "Engine.Sequence." + list));

            code.Emit(program.Set(item, "Engine.SequenceOp.bActive", [0x28], true));
            code.Emit(program.Set(item, "Engine.SequenceOp.ActivateCount", program.Integer(0)));
            if (IsA(item, "Engine.SeqAct_ActorFactory"))
            {
                code.Emit(program.Set(item, "Engine.SeqAct_ActorFactory.bIsSpawning", [0x28], true));
                code.Emit(program.Set(item, "Engine.SeqAct_ActorFactory.SpawnedCount", program.Integer(0)));
                code.Emit(program.Set(item, "Engine.SeqAct_ActorFactory.RemainingDelay", [0x1E, 0, 0, 0, 0]));
            }
            if (IsA(item, "ATDGame.ATDGame_SeqAct_EnemyWaveNode"))
                code.Emit(program.Set(item, "ATDGame.ATDGame_SeqAct_EnemyWaveNode.mTimeFlee", [0x1E, 0, 0, 0, 0]));
            if (IsA(item, "ATDGame.ATDGame_SeqAct_EnemyWaveDurationNode"))
                code.Emit(program.Set(item, "ATDGame.ATDGame_SeqAct_EnemyWaveDurationNode.mTimeFlee", [0x1E, 0, 0, 0, 0]));
            if (IsA(item, "ATDGame.ATDGame_SeqAct_ContinueNextWave"))
                code.Emit(program.Set(item, "ATDGame.ATDGame_SeqAct_ContinueNextWave.bHasBerserk", [0x28], true));
        }

        foreach (var value in saved.Values) code.Emit(program.Set(value.Target, value.Path, value.Value, value.Boolean));
        if (saved.Kind == 1)
        {
            foreach (uint item in objects.Where(item => IsA(item, "ATDGame.ATDGame_EnemySpawner") && IsWorldActor(item)))
                code.Emit(program.Call(item, "ATDGame.ATDGame_EnemySpawner.Reset"));
            foreach (uint item in objects.Where(item => IsA(item, "ATDGame.ATDGameRift") && IsWorldActor(item)))
                code.Emit(program.Call(item, "ATDGame.ATDGameRift.Reset"));

            uint manager = reader.U32(Address(gri, "ATDGame.ATDGameGameReplicationInfo.EnemySpawnerManager", "ObjectProperty", 4));

            code.Emit(program.Set(manager, "ATDGame.ATDGame_EnemyWaveManager.mCurrentRoundIndex", program.Integer(saved.PreviousRound)));
            code.Emit(program.Set(world.GameInfo, "ATDGame.ATDGameInfo.CurrentNumberOfEnemies", program.Integer(0)));
            code.Emit(program.Set(world.GameInfo, "ATDGame.ATDGameInfo.bRoundEnd", [0x28], true));
        }
        if (saved.Round is { } round)
        {
            code.Emit(program.Set(world.GameInfo, "LDGame.TGLDGame.RoundExistCount", program.Integer(0)));
            code.Emit(program.Set(world.GameInfo, "LDGame.TGLDGame.CurrentRound", program.Integer(round.Number)));
            code.Emit(program.Set(world.GameInfo, "LDGame.TGLDGame.RoundExistLimit", program.Integer(round.ExistLimit)));
            // Boss intro animation follows retry entry. Restore the map's actual time limit first so stale timing cannot trigger another loss during the animation.
            code.Emit(program.Call(gri, "LDGame.TGLDGameReplicationInfo.SetGameInfo", program.Integer(round.Number),
                program.Float(round.Time), program.Integer(round.SpawnLimit)));
        }

        code.Emit(program.Call(world.GameInfo, "PVEGame.PVEGame.RespawnDeadPlayer"));
        code.Emit(program.Call(world.GameInfo, "PVEGame.PVEGame.AllPlayerFullHealthAndAmmo"));
        foreach (var player in participants)
        {
            if (saved.Positions.TryGetValue(player.Uin, out byte[]? position))
                code.Emit(program.Context(program.Property(player.Controller, "Engine.Controller.Pawn"),
                    program.Final("Engine.Actor.SetLocation", [0x23, .. position]), program.Find("Engine.Actor.SetLocation.ReturnValue")));
            code.Emit(program.Set(player.Pri, "PVEGame.PVEPlayerReplicationInfo.ReviveCoinCount",
                program.Gateway(online, 14, program.Property(player.Pri, "UTGame.TGPlayerReplicationInfo.Uin"), program.Integer(0))));
        }

        code.Emit(program.Gateway(online, unchecked((int)CommandBase) + 5, program.Text(""), program.Integer(0)));
        code.Emit(program.Call(saved.Entry, "Engine.SequenceOp.ForceActivateInput", program.Integer(0)));
        code.Emit(program.Gateway(online, unchecked((int)CommandBase) + 5, program.Text(""), program.Integer(1)));
        program.Install(Restore, code.Finish());
    }

    private AfdevPveRetryProgram.Code ResumePlayers()
    {
        var code = new AfdevPveRetryProgram.Code();

        code.Emit(program.Set(world.WorldInfo, "Engine.WorldInfo.Pauser", [0x2A]));
        code.Emit(program.Set(gri, "PVEGame.PVEGameReplicationInfo.bSuspendGame", [0x28], true));
        foreach (var player in participants) code.Emit(program.Call(player.Controller, "PVEGame.PVEPlayerController.NotifyClientPlayerRespawned"));

        return code;
    }

    // Clear connection pulses left by the prior attempt. Write only existing reflected scalars, never relocating arrays, strings, or objects.
    // Call after confirmed deduction while the game thread still waits on the mailbox; the script then safely releases active queues.
    private void ResetImpulses(Checkpoint saved, bool apply = true)
    {
        string scope = program.Path(saved.Scope);

        foreach (uint item in program.Objects())
        {
            if (!IsA(item, "Engine.SequenceOp")) continue;

            string path = program.Path(item);

            if (path != scope && !path.StartsWith(scope + ".", StringComparison.Ordinal)) continue;

            foreach (var (list, structure) in new[] { ("InputLinks", "SeqOpInputLink"), ("OutputLinks", "SeqOpOutputLink") })
            {
                string owner = "Engine.SequenceOp." + structure;
                uint property = program.Find("Engine.SequenceOp." + list), inner = reader.U32(property + 0x84);
                uint stride = reader.U32(inner + 0x44), pointer = item + reader.U32(property + 0x60);
                uint data = Read(pointer), count = Read(pointer + 4);
                uint impulse = program.Find(owner + ".bHasImpulse");

                if (stride is < 16 or > 256 || count > 128 || (count > 0 && data < 0x10000) ||
                    program.Path(reader.U32(inner + 0x84)) != owner)
                    throw new InvalidDataException("PVE 序列连接布局不同。");

                for (uint index = 0; index < count; index++)
                {
                    uint address = data + index * stride + reader.U32(impulse + 0x60);

                    if (apply) Write(address, Read(address) & ~reader.U32(impulse + 0x84));
                    if (apply && list == "InputLinks")
                        Write(data + index * stride + reader.U32(program.Find(owner + ".QueuedActivations") + 0x60), 0);
                }
            }
        }
    }

    private Participant[] Participants(MatchStartMember[] members)
    {
        var result = new List<Participant>();

        foreach (uint item in program.Objects())
        {
            if (!IsA(item, "PVEGame.PVEPlayerController") || !IsWorldActor(item)) continue;

            uint pri = reader.U32(Address(item, "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));

            if (pri == 0 || Boolean(pri, "UTGame.TGPlayerReplicationInfo.bAlwaysObserver")) continue;
            if (!ulong.TryParse(ReadString(Address(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12)), NumberStyles.None, CultureInfo.InvariantCulture, out ulong uin) ||
                !members.Any(member => member.Profile.Uin == uin)) continue;

            result.Add(new(uin, item, pri));
        }

        if (result.GroupBy(player => player.Uin).Any(group => group.Count() != 1)) throw new InvalidDataException("PVE 重试真人身份重复。");

        return result.ToArray();
    }

    private bool IsA(uint item, string path)
    {
        uint type = program.Class(item);

        if (!types.TryGetValue((type, path), out bool matches))
        {
            var seen = new HashSet<uint>();
            uint parent = type;

            while (parent != 0)
            {
                if (seen.Count >= 32 || !seen.Add(parent)) throw new InvalidDataException("PVE 类继承链无效。");
                if (program.Path(parent) == path)
                {
                    matches = true;

                    break;
                }

                parent = reader.U32(parent + 0x48);
            }

            types[(type, path)] = matches;
        }

        return matches;
    }

    private bool IsWorldActor(uint item) => IsA(item, "Engine.Actor") && !Boolean(item, "Engine.Actor.bDeleteMe") &&
        program.Path(item).Contains(".TheWorld.PersistentLevel.", StringComparison.Ordinal);
    private uint Address(uint target, string path, string kind, uint width) => reader.FieldAddress(target, path.Split('.')[^1], path, kind, width);
    private int Int(uint target, string path) => (int)Read(Address(target, path, "IntProperty", 4));
    private bool Boolean(uint target, string path) => (Read(Address(target, path, "BoolProperty", 4)) & reader.U32(program.Find(path) + 0x84)) != 0;
    private string ActualEndGame() => world.MapId == 0x005F ? "TGIFGame.TGIF2Game.EndGameReason" : world.ModeId == 0x2002 ? "PVEGame.TGIFGame.EndGameReason" : "PVEGame.PVEGame.EndGameReason";
    private string ReadString(uint address)
    {
        uint count = Read(address + 4), capacity = Read(address + 8), data = Read(address);

        if (count == 0) return "";
        if (count > 1024 || capacity < count || data < 0x10000) throw new InvalidDataException("PVE 重试字符串边界无效。");

        return Encoding.Unicode.GetString(process.ReadMemory(data, checked((int)count * 2))).TrimEnd('\0');
    }

    private uint Read(uint address) => reader.U32(address);
    private void Write(uint address, uint value) => process.WriteMemory(address, BitConverter.GetBytes(value));
}
