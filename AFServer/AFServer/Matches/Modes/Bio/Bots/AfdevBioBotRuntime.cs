using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Bot roster and native-resource preparer for one ordinary BIO match. Installs only into the host's owned
/// suspended process; each slot prepares once after first binding. AFDEV owns world start, spawning, and
/// later gameplay. Creates no process, calls no StartMatch, and never frees native pages itself.
/// </summary>
/// <remarks>
/// The host serializes calls and cancellation. Unknown completion invalidates the preparer; the original
/// host then stops the entire Job.
/// </remarks>
public sealed partial class AfdevBioBotRuntime
{
    // -1 represents acceptance of the complete default Item input cache, not restored independent per-Item mapping.
    private static readonly ResourceExpectedInventory[] ExpectedInventory =
    [
        new(-1, 2, 0, "UTGameContent.TGWeap_HeartSeeker_Content", "默认 Item 集合 100013/100099/100009/100010 的原生加载实测缓存集合"),
        new(-1, 0, 35, "UTGameContent.TGWeap_AR15_Content", "默认 Item 集合 100013/100099/100009/100010 的原生加载实测缓存集合"),
        new(-1, 1, 1, "UTGameContent.TGWeap_USP_Content", "默认 Item 集合 100013/100099/100009/100010 的原生加载实测缓存集合"),
        new(-1, 3, 0, "UTGameContent.TGWeap_Grenade_Content", "默认 Item 集合 100013/100099/100009/100010 的原生加载实测缓存集合"),
        new(-1, 3, 15, "TGBioGameContent.TGBioGrenade_Content", "BIO PreLogin 的 3/15 参数及单键原生加载实测")
    ];
    private readonly AfdevOwnedProcess process;
    private readonly AfdevInstanceWorkspace workspace;
    private readonly Guid attemptId;
    private readonly Action<string> log;
    private readonly uint factoryCommand;
    private readonly uint resourceCommand;
    private readonly uint lifecycleCommand;
    private readonly uint trace;
    private readonly uint activationCommand;
    private uint controlCommand;
    private int preparationStarted;
    private AfdevNativeEvidence? world;
    private uint installedTickEntry;
    private byte[] installedTickWrapper = [];

    /// <summary>
    /// Validates this host's dormant thread gate so other modes can compose short native preparation calls
    /// before first publication.
    /// </summary>
    internal byte[] GetDormantTickCall(AfdevOwnedProcess ownedProcess, AfdevNativeEvidence evidence)
    {
        byte[] patch = TickHook.CallPatch(installedTickEntry);

        if (!ReferenceEquals(process, ownedProcess) || evidence.AttemptId != attemptId ||
            evidence.ProcessId != process.ProcessId || evidence.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            preparationStarted != 0 || installedTickEntry == 0 || installedTickWrapper.Length == 0 ||
            !process.ReadMemory(TickHook.CallSite, 5).AsSpan().SequenceEqual(patch) ||
            !process.ReadMemory(installedTickEntry, installedTickWrapper.Length).AsSpan().SequenceEqual(installedTickWrapper))
            throw new InvalidDataException("休眠 BIO 线程门不属于当前宿主或已改变。");

        return patch;
    }

    private AfdevBioBotRuntime(AfdevOwnedProcess process, AfdevInstanceWorkspace workspace, Guid attemptId,
        Action<string> log, uint factoryCommand, uint resourceCommand, uint lifecycleCommand, uint trace,
        uint activationCommand)
    {
        this.process = process;
        this.workspace = workspace;
        this.attemptId = attemptId;
        this.log = log;
        this.factoryCommand = factoryCommand;
        this.resourceCommand = resourceCommand;
        this.lifecycleCommand = lifecycleCommand;
        this.trace = trace;
        this.activationCommand = activationCommand;
    }

    /// <summary>
    /// Installs the fixed game-thread gate before first ResumeMainThread. Validate before bootstrap
    /// rewrites native ranges. On failure, pages remain owned by the original process for whole-Job
    /// cleanup; never reinstall into that process.
    /// </summary>
    public static AfdevBioBotRuntime InstallSuspended(AfdevOwnedProcess process, AfdevInstanceWorkspace workspace,
        Guid attemptId, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(workspace);

        if (attemptId == Guid.Empty || workspace.InstanceId == Guid.Empty ||
            !AfdevMapCatalog.IsSupported(workspace.ModeId, workspace.MapId))
            throw new ArgumentException("Bot 原生桥必须属于精确的普通 BIO 物理实例和启动尝试。");

        string imageSha256 = AfdevBotNativeImage.VerifySuspended(process, workspace.ExecutablePath);

        AfdevNavigationNativeImage.Verify(process);

        const int slotCapacity = AfdevBioBotPlan.MaximumFighterCapacity - 1;
        const int wrapperCapacity = 65536 * slotCapacity;
        uint code = process.AllocateMemory(wrapperCapacity);
        uint trace = process.AllocateMemory(4096, executable: false);
        var slots = new AfdevBioBotRuntime[slotCapacity];

        uint Allocate(int size = 4096)
        {
            uint page = process.AllocateMemory(size, executable: false);

            process.WriteMemory(page, new byte[size]);
            return page;
        }

        process.WriteMemory(trace, new byte[4096]);

        for (int index = 0; index < slots.Length; index++)
        {
            uint factory = Allocate();
            uint resources = Allocate();
            uint lifecycle = Allocate();
            uint activation = Allocate(AfdevBioBotActivationHook.PageSize);
            var slot = new AfdevBioBotRuntime(process, workspace, attemptId,
                log ?? (_ => { }), factory, resources, lifecycle, trace, activation)
            {
                slotIndex = index,
                controlCommand = Allocate(AfdevBioBotControlHook.PageSize),
                combatCommand = Allocate(AfdevBioBotCombatHook.PageSize)
            };

            slot.ConfigureNavigation(Allocate(), Allocate(), Allocate(), Allocate());
            slots[index] = slot;
        }

        AfdevBioBotRuntime result = slots[0];
        var physicsObservers = new AfdevBioBotPhysicsObservers(process, trace,
            slots.Select(slot => slot.motionCommand).ToArray());

        result.rosterSlots = slots;

        uint preparationCommand = Allocate(AfdevBioBotPreparationHook.PageSize);
        byte[] preparationStartCode = AfdevBioBotPreparationHook.BuildStart(preparationCommand,
            slots.Select(slot => slot.activationCommand).ToArray());
        uint preparationStartEntry = process.AllocateMemory(preparationStartCode.Length);

        process.WriteMemory(preparationStartEntry, preparationStartCode);

        if (!process.ReadMemory(preparationStartEntry, preparationStartCode.Length).AsSpan().SequenceEqual(preparationStartCode))
            throw new InvalidDataException("Bot 名册准备协调入口回读不一致。");

        byte[] scriptDispatchCode = AfdevBioBotScriptDispatchHook.Build(preparationCommand, preparationStartEntry);
        uint scriptDispatchEntry = process.AllocateMemory(scriptDispatchCode.Length);
        byte[] scriptDispatchPatch = AfdevBioBotScriptDispatchHook.CallPatch(scriptDispatchEntry);

        // ProcessEvent dispatches through UFunction's execution pointer; ordinary-script CallFunction calls ProcessInternal directly.
        // Complete exact dispatch at the shared site after argument consumption without changing script flags, bytecode, or FFrame ownership.
        if (!process.ReadMemory(AfdevBioBotScriptDispatchHook.CallSite, 5).AsSpan().SequenceEqual(
                AfdevBioBotScriptDispatchHook.CallPatch(AfdevBioBotTimerHook.OriginalExec)))
            throw new InvalidDataException("Bot 脚本开局分派的原生调用点与审核映像不一致。");

        process.WriteMemory(scriptDispatchEntry, scriptDispatchCode);

        if (!process.ReadMemory(scriptDispatchEntry, scriptDispatchCode.Length).AsSpan().SequenceEqual(scriptDispatchCode))
            throw new InvalidDataException("Bot 脚本开局分派入口回读不一致。");

        process.WriteMemory(AfdevBioBotScriptDispatchHook.CallSite, scriptDispatchPatch);

        if (!process.ReadMemory(AfdevBioBotScriptDispatchHook.CallSite, scriptDispatchPatch.Length).AsSpan().SequenceEqual(scriptDispatchPatch))
            throw new InvalidDataException("Bot 脚本开局分派调用点安装回读不一致。");

        foreach (AfdevBioBotRuntime slot in slots)
        {
            slot.rosterOwner = result;
            slot.physicsObservers = physicsObservers;
            slot.preparationCommand = preparationCommand;
            slot.preparationStartEntry = preparationStartEntry;
        }

        result.InstallOnlineIsolationSuspended();

        byte[] timerCode = AfdevBioBotTimerHook.Build(slots.Select(slot => slot.activationCommand).ToArray());
        uint timerEntry = process.AllocateMemory(timerCode.Length);

        process.WriteMemory(timerEntry, timerCode);

        if (!process.ReadMemory(timerEntry, timerCode.Length).AsSpan().SequenceEqual(timerCode))
            throw new InvalidDataException("Bot 名册的共享原生 Timer 入口回读不一致。");

        byte[] wrapper = AfdevBioBotPreparationHook.Append(TickHook.Build(code, trace), preparationCommand);

        foreach (AfdevBioBotRuntime slot in slots)
        {
            slot.sharedTimerEntry = timerEntry;
            wrapper = FactoryHook.Append(wrapper, trace, slot.factoryCommand);
            wrapper = ResourceCallHook.Append(wrapper, trace, slot.factoryCommand, slot.resourceCommand);
            wrapper = LifecycleCallHook.Append(wrapper, trace, slot.factoryCommand, slot.lifecycleCommand);
            wrapper = StateProgramHook.Append(wrapper, trace, slot.factoryCommand, slot.navigationCommand);
            wrapper = AfdevBioBotMotionHook.Append(wrapper, trace, slot.factoryCommand, slot.motionCommand);
            wrapper = AfdevBioBotNavigationStopHook.Append(wrapper, trace, slot.factoryCommand, slot.navigationStopCommand);
            wrapper = AfdevBioBotReachHook.Append(wrapper, trace, slot.reachCommand);
            wrapper = slot.AppendOnlineIdentityConfirmation(wrapper);
            wrapper = AfdevBioBotControlHook.Append(wrapper, slot.controlCommand);
            wrapper = AfdevBioBotCombatHook.Append(wrapper, trace, slot.combatCommand);
        }

        // All slots share one World.Tick call. Each inserted gate corrects the same original call's relative address.
        foreach (AfdevBioBotRuntime slot in slots)
            wrapper = AfdevBioBotActivationHook.Append(wrapper, slot.activationCommand);

        byte[] patch = TickHook.CallPatch(code);

        if (wrapper.Length > wrapperCapacity || !process.ReadMemory(TickHook.CallSite, 5).AsSpan().SequenceEqual(Convert.FromHexString("E80E290D00")))
            throw new InvalidDataException("Bot 安装前的 tick 入口或包装边界发生变化。");

        process.WriteMemory(code, wrapper);
        process.WriteMemory(TickHook.CallSite, patch);
        result.installedTickEntry = code;
        result.installedTickWrapper = wrapper;

        if (!process.ReadMemory(code, wrapper.Length).AsSpan().SequenceEqual(wrapper) ||
            !process.ReadMemory(TickHook.CallSite, patch.Length).AsSpan().SequenceEqual(patch))
            throw new InvalidDataException("Bot 游戏线程门安装回读不一致。");

        result.SaveRoster("installed", new InstallSuspendedJson1
        {
            InstanceId = workspace.InstanceId, attemptId = attemptId, ProcessId = process.ProcessId, CreationTimeUtc = process.CreationTimeUtc,
            code = code, trace = trace, timerEntry = timerEntry, preparationCommand = preparationCommand, preparationStartEntry = preparationStartEntry, scriptDispatchEntry = scriptDispatchEntry,
            ScriptDispatchCallSite = AfdevBioBotScriptDispatchHook.CallSite,
            ScriptDispatchSha256 = TickHook.Hash(scriptDispatchCode), wrapperCapacity = wrapperCapacity, SlotCapacity = slots.Length,
            Slots = slots.Select(slot => new InstallSuspendedJson2 { slotIndex = slot.slotIndex, factoryCommand = slot.factoryCommand, resourceCommand = slot.resourceCommand,
                lifecycleCommand = slot.lifecycleCommand, activationCommand = slot.activationCommand, navigationCommand = slot.navigationCommand, motionCommand = slot.motionCommand,
                reachCommand = slot.reachCommand, navigationStopCommand = slot.navigationStopCommand, controlCommand = slot.controlCommand, combatCommand = slot.combatCommand,
                onlineIsolationPage = slot.onlineIsolationPage }).ToArray(),
            WrapperSha256 = TickHook.Hash(wrapper), ImageSha256 = imageSha256, AuditedImageSha256 = TickHook.ImageHash,
            PagesRetainedUntilOwnedJobStops = true
        });

        return result;
    }

    /// <summary>
    /// Creates this slot's internal identity within the first BIO world's native-preparation boundary,
    /// allowing human loading to interleave. One call, no retry. The native transaction allocates PlayerID,
    /// marks the bot, and copies its nickname without increasing human/bot counts during preparation.
    /// </summary>
    private async Task<AfdevBioBotPreparation> PrepareParticipantAsync(AfdevNativeEvidence boundWorld,
        AfdevBioBotPlan plan, AfdevBioBotIdentity identity, CancellationToken cancellationToken, bool initializeNativeControl)
    {
        ArgumentNullException.ThrowIfNull(boundWorld);
        ArgumentNullException.ThrowIfNull(plan);

        string botUin = identity.Uin;
        string nickname = identity.Nickname;

        ArgumentException.ThrowIfNullOrWhiteSpace(botUin);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);

        if (identity.BotId == Guid.Empty || plan.AllocationId != boundWorld.AllocationId || plan.RoomId == 0 ||
            plan.Bots[slotIndex] != identity || botUin != "bot-" + identity.BotId.ToString("N")[..12] ||
            botUin.Length != 16 || !botUin.StartsWith("bot-", StringComparison.Ordinal) ||
            botUin.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("Bot 身份必须属于独立的 bot-xxxxxxxxxxxx 域。", nameof(botUin));
        if (Interlocked.CompareExchange(ref preparationStarted, 1, 0) != 0)
            throw new InvalidOperationException("同一物理实例的 Bot 准备已开始，不能重试或重新绑定。");

        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workStopping.Token);

        cancellationToken = preparing.Token;
        world = boundWorld;
        CheckBinding();
        VerifyUinAvailable(botUin);
        cancellationToken.ThrowIfCancellationRequested();

        uint controller = await FactoryRun.ExecuteAsync(process, boundWorld, factoryCommand, Save, log, cancellationToken,
            preparationCommand: preparationCommand).ConfigureAwait(false);

        PublishOnlineIdentity(boundWorld, controller, identity);

        ResourceInitialization initialized = await ResourceCallRun.ExecuteAsync(process, boundWorld, controller,
            resourceCommand, Save, log, cancellationToken, botUin, preparationCommand: preparationCommand).ConfigureAwait(false);

        await ConfirmOnlineIdentityAsync(cancellationToken).ConfigureAwait(false);

        if (initializeNativeControl)
        {
            await AfdevBioBotControlInitialization.ExecuteAsync(process, boundWorld, controller, identity.BotId,
                botUin, onlineIsolationPage, controlCommand, Save, CheckBinding, cancellationToken,
                preparationCommand: preparationCommand).ConfigureAwait(false);
            log("BIO_BOT_CONTROL_INITIALIZED 精确 BIO PC 已在出生和装备之前初始化原生控制位，等待行为对照。");
        }

        var inspector = new BioResourceInspector(process);

        inspector.Prepare(boundWorld, controller);

        ResourceAvatarResult avatar = inspector.VerifyDefaultAvatar(botUin, 0, initialized.RecoverReturnedTrue);

        if (!avatar.Complete)
            throw new InvalidDataException("Bot 默认外观与背包资料未完成原生恢复。");

        async Task<LifecycleCallResult> Call(LifecycleOperation operation)
        {
            CheckBinding();

            return await LifecycleCallRun.ExecuteAsync(process, boundWorld, controller, lifecycleCommand,
                operation, Save, log, cancellationToken, botUin, nickname,
                preparationCommand: preparationCommand).ConfigureAwait(false);
        }

        LifecycleCallResult allocatedIdentity = await Call(LifecycleOperation.AllocatePlayerIdentity).ConfigureAwait(false);

        await Call(LifecycleOperation.SetPlayerName).ConfigureAwait(false);

        LifecycleCallResult loaded = await Call(LifecycleOperation.HasClientLoadedCurrentWorld).ConfigureAwait(false);

        if (loaded.Observation.Result != 1)
            throw new InvalidDataException("AFDEV 尚未确认 Bot 的当前世界加载完成。");

        ResourceInventoryResult inventory = inspector.VerifyInventoryClasses([]);

        if (inventory.Manager == null)
            await Call(LifecycleOperation.SpawnInventoryManager).ConfigureAwait(false);

        await Call(LifecycleOperation.LoadDefaultInventory).ConfigureAwait(false);
        await Call(LifecycleOperation.LoadBioThrowInventory).ConfigureAwait(false);
        inventory = inspector.VerifyInventoryClasses(ExpectedInventory);

        if (!inventory.Complete)
            throw new InvalidDataException("Bot 默认武器或 BIO 专用投掷物未完成原生加载。");

        LifecycleCallResult team = await Call(LifecycleOperation.ChangeTeam).ConfigureAwait(false);
        ResourceProfile profile = inspector.Capture();

        if (team.Observation.Result != 1 || profile.Team == 0 || profile.HudCamp != 0 ||
            inspector.Reader.U32(inspector.Reader.FieldAddress(profile.Team,
                "TeamIndex", "Engine.TeamInfo.TeamIndex", "IntProperty", 4)) != 0)
            throw new InvalidDataException("AFDEV 未将准备中的 Bot 放入普通 BIO 人类阵营。");

        await Call(LifecycleOperation.PostLoadCharacter).ConfigureAwait(false);

        ResourceSkillResult skills = inspector.VerifySkills();

        if (!skills.Complete)
            throw new InvalidDataException("Bot 的五项 BIO 技能或其原生所有权尚未完成。");

        await Call(LifecycleOperation.SetProcessingComplete).ConfigureAwait(false);
        await Call(LifecycleOperation.BaseNotifyLoadingComplete).ConfigureAwait(false);

        ResourceLoadingState loading = inspector.CaptureLoadingState();

        bool Bit(string suffix) => loading.Bits.Single(bit => bit.Path.EndsWith("." + suffix, StringComparison.Ordinal)).Value;

        if (!loading.LoadingTimerAbsent || !Bit("bInitialProcessingComplete") || !Bit("bLoadingComplete") ||
            Bit("bWaitingLoadingProcessComplete") || Bit("bOnlySpectator"))
            throw new InvalidDataException("Bot 的本地原生加载完成门尚未满足。");

        LifecycleCallResult canRestart = await Call(LifecycleOperation.CanRestartPlayer).ConfigureAwait(false);

        if (canRestart.Observation.Result != 1)
            throw new InvalidDataException("AFDEV CanRestartPlayer 拒绝已准备的 Bot。");

        CheckBinding();
        profile = inspector.Capture();

        var result = new AfdevBioBotPreparation(boundWorld.InstanceId, boundWorld.AttemptId, boundWorld.AllocationId,
            boundWorld.WorldGeneration, boundWorld.ProcessId, boundWorld.ProcessCreationTimeUtc, boundWorld.World,
            boundWorld.GameInfo, controller, profile.Pri.Address, allocatedIdentity.Observation.Result,
            botUin, nickname, DateTimeOffset.UtcNow) { BotId = identity.BotId };

        Save("prepared", new PrepareParticipantAsyncJson1 { result = result, Checks = avatar.Checks, inventory = inventory, skills = skills, loading = loading, Tick = TickHook.Read(process, trace) });
        log($"BIO_BOT_PREPARED instance={result.InstanceId} allocation={result.AllocationId} slot={slotIndex} bot={identity.BotId:N} controller=0x{controller:X8}; 仅准备完成，激活尚未验收。");
        preparedBot = result;

        return result;
    }

    private void CheckBinding()
    {
        AfdevNativeEvidence bound = world ?? throw new InvalidOperationException("Bot 尚未绑定真实世界。");
        var reader = new QueryInspector(process);

        if (!process.IsAlive || bound.InstanceId != workspace.InstanceId || bound.AttemptId != attemptId ||
            bound.ProcessId != process.ProcessId || bound.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            bound.AllocationId == Guid.Empty || bound.WorldGeneration != 1 || !AfdevMapCatalog.MatchesBio(bound) ||
            !AfdevBotNativeImage.IsVerifiedFor(process, bound.AfdevSha256) || reader.U32(0x02066BF8) != bound.World ||
            reader.U32(bound.WorldInfo + 0x414) != bound.GameInfo || reader.Identity(bound.GameInfo).Class != bound.GameInfoClass)
            throw new InvalidDataException("Bot 资源操作已失去精确的物理进程、启动尝试、allocation 或世界绑定。");

        CheckOnlineIsolation();
    }

    private void VerifyUinAvailable(string botUin)
    {
        AfdevNativeEvidence bound = world ?? throw new InvalidOperationException("Bot 尚未绑定世界。");
        var reader = new QueryInspector(process);
        uint level = reader.U32(bound.World + 0x50);
        uint data = reader.U32(level + 0x3C);
        uint count = reader.U32(level + 0x40);

        if (data == 0 || count > 200000 || reader.U32(level + 0x44) < count)
            throw new InvalidDataException("Bot 身份查重时 Actor 数组边界发生变化。");

        byte[] actors = process.ReadMemory(data, checked((int)count * 4));

        for (int index = 0; index < count; index++)
        {
            uint actor = BinaryPrimitives.ReadUInt32LittleEndian(actors.AsSpan(index * 4, 4));

            if (actor == 0 || !reader.IsA(actor, "UTGame.TGPlayerReplicationInfo"))
                continue;

            reader.Identity(actor);

            uint field = reader.FieldAddress(actor, "Uin", "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
            uint stringData = reader.U32(field);
            uint stringCount = reader.U32(field + 4);
            uint capacity = reader.U32(field + 8);

            if (stringCount == 0)
                continue;
            if (stringData == 0 || stringCount > 128 || capacity < stringCount)
                throw new InvalidDataException("现存 PRI 的身份字符串边界无效。");

            byte[] raw = process.ReadMemory(stringData, checked((int)stringCount * 2));

            if (raw[^1] != 0 || raw[^2] != 0)
                throw new InvalidDataException("现存 PRI 的身份字符串未终止。");
            if (Encoding.Unicode.GetString(raw, 0, raw.Length - 2) == botUin)
                throw new InvalidDataException("本世界中已经存在同一内部 Bot 身份。");
        }

        CheckBinding();
    }

    private void Save(string name, object value)
    {
        string directory = Path.Combine(workspace.Directory, "logs", "bio-bot", $"slot-{slotIndex:D2}");

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Native identity, resources, and loading state are prepared. Count activation, client visibility, and
/// combat behavior require separate acceptance.
/// </summary>
public sealed record AfdevBioBotPreparation(Guid InstanceId, Guid AttemptId, Guid AllocationId, long WorldGeneration,
    uint ProcessId, DateTimeOffset ProcessCreationTimeUtc, uint World, uint GameInfo, uint Controller, uint Pri,
    int PlayerId, string Uin, string Nickname, DateTimeOffset PreparedAtUtc)
{
    /// <summary>
    /// Internal identity frozen for this round. The controller, PRI, and actions belong only to this bot.
    /// </summary>
    public Guid BotId { get; init; }
}
