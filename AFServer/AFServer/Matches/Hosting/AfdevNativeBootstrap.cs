using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Migrates required BIO initialization from the verified PH loader into the host, touching only this
/// host's owned x86 process.
/// </summary>
/// <remarks>
/// Addresses come from AFDevLoader_v48_spawner_multi_instance.py and were checked byte-for-byte against the
/// original b4273f26…b51 image. Native code/data pages are reclaimed with the whole process. Cancellation
/// or native-call failure forbids continuing initialization in that same process.
/// </remarks>
public sealed partial class AfdevNativeBootstrap
{
    internal const uint GEngine = 0x02063820;
    internal const uint GLog = 0x01F9D420;
    internal const uint GWorld = 0x02066BF8;
    internal const uint ServerMove = 0x013A88D0;
    private const uint GNames = 0x02041B68;
    internal const string ZeroScheduleSha256 = "69302223fb2deffef6e60657342637b14161ad749f8f379bd2d7ece658b14623";
    private readonly Action<string> log;
    private readonly List<(uint Address, byte[] Expected)> installed = [];
    private bool prepared;
    private bool initialized;
    private bool initializeStarted;
    private bool enginePrepared;
    private bool routeSelected;
    private uint capacityGameInfo;
    private uint maxPlayersAddress;
    private uint maxPlayersAllowedAddress;
    private bool invocationFaulted;
    private uint serverMoveTarget;
    private uint marker;
    private uint trace;
    private uint invocationPage;
    private byte[] serverMoveJump = [];
    private AfdevNativeFaultGuard? faultGuard;
    private readonly HashSet<string> reportedNames = new(StringComparer.Ordinal);

    private static readonly (uint Address, string Original, string Replacement, string Name)[] Patches =
    [
        (0x014972F1, "A120D4F901", "E917010000", "TCLS LoginInfo bypass"),
        (0x01494FF0, "81ECC8010000A1A0", "31C0C21400909090", "DevLogin offline failure"),
        (0x013494DE, "891DCC69FD01", "909090909090", "GIsServer final reset"),
        (0x013494D8, "8935C869FD01", "891DC869FD01", "GIsClient final write")
    ];
    private static readonly (uint Address, string Original)[] Stages =
    [
        (0x009C8A0C, "68E0BBB401"), (0x009C8B37, "6888BCB401"),
        (0x009C91D1, "6828C0B401"), (0x009C966D, "68D8C1B401"),
        (0x009C99AA, "6888C2B401"), (0x009C9E44, "6870C3B401"),
        (0x009CAAAF, "68F0C6B401")
    ];
    private static readonly (uint Address, string Original)[] Anchors =
    [
        (0x015B8980, "6AFF68286D9A0164A1000000005083EC14"),
        (0x004D4343, "A16C1B04023BF8"),
        (0x004D4422, "8B04AD0096FF018B0D681B04028904B9"),
        (0x004D445C, "8B46088B4C2418D1F8"),
        (0x004D42CE, "F646080174178D56105552FFD3"),
        (0x004C8F56, "8D580183C6020FAFF383C61056B9741B0402E8E3E5FFFF"),
        (0x004C7678, "8B068D0C28890E"),
        (0x00D9C1CD, "8B71508B464083C63C"),
        (0x00D9C2A9, "83BC243C060000008B368B3E"),
        (0x00DA2D84, "8B542424898214040000"),
        (0x01349D75, "68982FD1016A0133FF57FF1514639F01"),
        (0x004039C0, "E97BD8FFFF"),
        (0x00401240, "833D08D0F90100740FE802FEFFFFC70508D0F90100000000"),
        (0x00401299, "8B5C240CC783000200000A000000"),
        (0x01277EDB, "8D7E1C6A0057C744242800000000C7061042CD01894614C7461801000000"),
        (0x01277F01, "833DCC69FD01006880000000740768A40DDC01EB0A8B0DC482070283C15C5157E89ABA18FF"),
        (0x01DC0DA4, "7467616D651FE92C9A971A0CD1F610FB")
    ];

    /// <summary>
    /// Binds deployment, startup attempt, and exact process. Warmup prepares only the engine; select this
    /// match's integrated route before the first OPEN.
    /// </summary>
    public AfdevNativeBootstrap(AfdevOwnedProcess process, AfdevInstanceWorkspace workspace,
        AfdevRuntimeValidation validation, Guid attemptId, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(validation);

        if (attemptId == Guid.Empty || workspace.InstanceId == Guid.Empty ||
            !AfdevMapCatalog.IsSupported(workspace.ModeId, workspace.MapId) ||
            validation.Target != AfdevMapCatalog.Resolve(workspace.ModeId, workspace.MapId) ||
            workspace.ModeId != validation.ModeId || workspace.MapId != validation.MapId ||
            workspace.ManifestVersion != validation.Manifest.RuntimeVersion)
            throw new InvalidDataException("原生初始化必须绑定当前清单中已接入的精确玩法与地图路由。");

        Process = process;
        Workspace = workspace;
        Validation = validation;
        AttemptId = attemptId;
        this.log = log ?? (_ => { });
    }

    internal AfdevOwnedProcess Process { get; }
    internal AfdevInstanceWorkspace Workspace { get; }
    internal AfdevRuntimeValidation Validation { get; private set; }
    internal Guid AttemptId { get; }
    internal bool Initialized => initialized;
    internal byte FighterCapacity { get; private set; }
    internal uint Stage => ReadUInt32(marker);
    internal uint StageMask => ReadUInt32(marker + 4);
    internal uint SetGameInfoCount => ReadUInt32(trace);
    internal uint SetGameInfoWorld => ReadUInt32(trace + 4);

    /// <summary>The latest unmet actual native condition, retained in host timeout diagnostics.</summary>
    public string WaitingCondition { get; internal set; } = "suspended native verification";

    /// <summary>
    /// Call before first resuming the main thread. Validates all original addresses before installing
    /// required patches and side-effect-free milestones.
    /// </summary>
    public void PrepareSuspended()
    {
        if (prepared) throw new InvalidOperationException("同一启动尝试不能重复准备原生补丁。");

        var identity = AfdevBinaryInspector.Inspect(Workspace.ExecutablePath);
        var mutexOriginal = Encoding.Unicode.GetBytes("TGAME_{D21F20CD-996C-4ae2-8BF8-F2A7B4CD20D5}\0");
        var mutexReplacement = Encoding.Unicode.GetBytes("TGAME_{" + AttemptId.ToString("D").ToUpperInvariant() + "}\0");

        Require(identity.AfdevSha256 == Validation.Manifest.AfdevSha256 &&
            identity.ServerMoveV4Sha256 == Validation.Manifest.ServerMoveV4Sha256, "实例 EXE 与本次 manifest 不一致。");
        VerifyKnownCode();
        foreach (var anchor in Anchors) Verify(anchor.Address, Convert.FromHexString(anchor.Original));
        foreach (var patch in Patches) Verify(patch.Address, Convert.FromHexString(patch.Original));
        foreach (var stage in Stages) Verify(stage.Address, Convert.FromHexString(stage.Original));

        Verify(0x009C9627, Convert.FromHexString("E834913D00"));
        Verify(0x01D12F98, mutexOriginal);
        Require(ReadUInt32(0x020D4B80) == 0, "IsBootFromTCLS 原始值不为零，拒绝未知启动状态。");
        serverMoveJump = Process.ReadMemory(ServerMove, 5);
        Require(serverMoveJump[0] == 0xE9, "原生 ServerMove v4 JMP 缺失。");
        serverMoveTarget = unchecked(ServerMove + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(serverMoveJump.AsSpan(1)));
        VerifyServerMove();
        faultGuard = new AfdevNativeFaultGuard(Process);

        foreach (var patch in Patches)
        {
            Install(patch.Address, Convert.FromHexString(patch.Replacement));
            log($"NATIVE_PATCH {patch.Name} address=0x{patch.Address:X8} original={patch.Original}");
        }

        Install(0x01D12F98, mutexReplacement);
        InstallLoadMapHooks();
        InstallSetGameInfoHook();
        InstallListenPortHook();
        prepared = true;
        log($"NATIVE_PREPARED attempt={AttemptId:N} pid={Process.ProcessId} servermove=0x{serverMoveTarget:X8}");
    }

    /// <summary>
    /// Waits for engine initialization and stabilization without BIO OPEN, bounded by the current attempt's
    /// cancellation and timeout.
    /// </summary>
    public async Task PrepareEngineAsync(CancellationToken cancellationToken)
    {
        if (!prepared || enginePrepared || initializeStarted) throw new InvalidOperationException("原生初始化必须且只能跟随一次挂起准备。");

        WaitingCondition = "GEngine/GLog publication";
        VerifyNoNativeFailure();

        while (ReadUInt32(GEngine) == 0 || ReadUInt32(GLog) == 0)
            await WaitForProgressAsync(cancellationToken);

        log($"ENGINE_PUBLISHED engine=0x{ReadUInt32(GEngine):X8} log=0x{ReadUInt32(GLog):X8}");

        var settling = System.Diagnostics.Stopwatch.StartNew();

        while (settling.Elapsed < TimeSpan.FromSeconds(3)) await WaitForProgressAsync(cancellationToken);

        VerifyDedicatedMode();
        enginePrepared = true;
        VerifyEngineReady();
    }

    /// <summary>
    /// Warmup verifies only that the engine can accept its first OPEN; the frontend world is not a playable
    /// BIO world.
    /// </summary>
    internal void VerifyEngineReady()
    {
        Require(enginePrepared && !initializeStarted && Process.IsAlive, "原生引擎不能再接受首次 OPEN。");
        VerifyNoNativeFailure();
        VerifyDedicatedMode();
        VerifyServerMove();
        VerifyInstalledCode();
        Require(ReadUInt32(GEngine) != 0 && ReadUInt32(GLog) != 0, "预热引擎全局对象已失效。");
        Require(ReadUInt32(ReadUInt32(GEngine) + 0x678) == 0, "预热引擎仍在处理 PendingURL。");
    }

    /// <summary>
    /// Binds the map only before the first OPEN. The physical workspace keeps its resource lease; once
    /// selected, it cannot switch maps or serve another round.
    /// </summary>
    internal void SelectRoute(uint modeId, ushort mapId)
    {
        if (routeSelected)
            throw new InvalidOperationException("本次原生世界已经选择地图，不能重复绑定或切换路由。");

        VerifyEngineReady();
        Validation = Validation.SelectRoute(modeId, mapId);
        routeSelected = true;
        log($"NATIVE_ROUTE_BOUND mode=0x{modeId:X4} map=0x{mapId:X4} package={Validation.Target.MapName} game={Validation.Target.GameClass}");
    }

    /// <summary>
    /// Keeps four-player capacity for direct-launch diagnostics. Production starts consume the round's
    /// frozen snapshot through the capacity overload.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken)
        => InitializeAsync(4, cancellationToken);

    /// <summary>
    /// Passes frozen participant capacity to the first OPEN. Warmup does not fix player count early; this
    /// cannot reset an old match or reuse it across rounds.
    /// </summary>
    public async Task InitializeAsync(byte fighterCapacity, CancellationToken cancellationToken)
    {
        AfdevMatchStartValidator.ValidateCapacity(Validation.ModeId, Validation.MapId, fighterCapacity);

        if (!enginePrepared) await PrepareEngineAsync(cancellationToken);

        VerifyEngineReady();

        if (!routeSelected)
            SelectRoute(Validation.ModeId, Validation.MapId);

        initializeStarted = true;
        FighterCapacity = fighterCapacity;
        WaitingCondition = "game-thread OPEN completion";

        uint page = Process.AllocateMemory(4096);
        string command = $"OPEN {Validation.Target.MapName}?game={Validation.Target.GameClass}?listen?MaxPlayers={fighterCapacity}";
        var code = new X86Code();

        Process.WriteMemory(page, Encoding.Unicode.GetBytes(command + "\0"));
        // Reset and OPEN execute on the same game thread so old frontend milestones cannot become evidence for the new map.
        code.Store(marker, 0).Store(marker + 4, 0).Store(trace, 0).Store(trace + 4, 0);
        AppendListenPortTraceReset(code);
        code.Byte(0xA1).UInt32(GEngine).Hex("8D483CA1").UInt32(GLog).Byte(0x50);
        code.Byte(0x68).UInt32(page).Byte(0xB8).UInt32(0x015B8980).Hex("FFD0");

        openStartedUtc = DateTimeOffset.UtcNow;
        CaptureReadinessDiagnostic("open-starting", force: true);

        uint result = await InvokeMainThreadAsync(code.ToArray(), cancellationToken);

        openCompletedUtc = DateTimeOffset.UtcNow;
        Require(result != 0, "UTGameEngine::Exec 拒绝了本次 OPEN。");
        initialized = true;
        WaitingCondition = "post-OPEN LoadMap stage 7";
        log($"OPEN_EXECUTED result={result} command={command}");
    }

    /// <summary>
    /// Checks actual engine capacity once the world is ready. Native clamping or later changes cannot count
    /// as correct filling for this round.
    /// </summary>
    internal void VerifyFighterCapacity(uint gameInfo)
    {
        Require(initialized && FighterCapacity != 0 && IsPointer(gameInfo), "参战容量只能在本次 OPEN 的有效 GameInfo 上核验。");

        bool firstCheck = capacityGameInfo == 0;

        if (firstCheck)
        {
            maxPlayersAddress = gameInfo + FindPropertyOffset(gameInfo, "MaxPlayers", "IntProperty");
            maxPlayersAllowedAddress = gameInfo + FindPropertyOffset(gameInfo, "MaxPlayersAllowed", "IntProperty");
            capacityGameInfo = gameInfo;
        }

        Require(gameInfo == capacityGameInfo, "参战容量证据不能跨 GameInfo 或世界复用。");

        uint actual = ReadUInt32(maxPlayersAddress);
        uint allowed = ReadUInt32(maxPlayersAllowedAddress);

        Require(actual == FighterCapacity && allowed >= FighterCapacity,
            $"原生参战容量与冻结快照不一致：expected={FighterCapacity} MaxPlayers={actual} MaxPlayersAllowed={allowed}。");

        if (firstCheck)
            log($"NATIVE_CAPACITY expected={FighterCapacity} max_players={actual} max_players_allowed={allowed}");
    }

    internal void VerifyDedicatedMode()
    {
        uint client = ReadUInt32(0x01FD69C8);
        uint server = ReadUInt32(0x01FD69CC);
        uint editor = ReadUInt32(0x01FD69AC);

        Require(client == 0 && server == 1 && editor == 0,
            $"原生 dedicated mode 不匹配：client={client} server={server} editor={editor}。");
    }

    internal void VerifyServerMove()
    {
        Verify(ServerMove, serverMoveJump);
        Require(Hash(Process.ReadMemory(serverMoveTarget, 639)) == Validation.Manifest.ServerMoveV4Sha256,
            "当前原生 ServerMove v4 完整函数体与本次清单不匹配。");
    }

    internal void VerifyInstalledCode()
    {
        faultGuard?.VerifyHooks();
        VerifyListenPortAnchors();
        VerifyDateTimeCode();
        foreach (var patch in installed) Verify(patch.Address, patch.Expected);
        foreach (var anchor in Anchors) Verify(anchor.Address, Convert.FromHexString(anchor.Original));
    }

    internal bool MatchesName(uint value, string expected)
    {
        if (!MatchesNameSnapshot(Process.ReadMemory, value, expected)) return false;
        if (reportedNames.Contains(expected)) return true;

        uint index = ReadUInt32(value + 0x2C);
        uint data = ReadUInt32(GNames);

        if ((ulong)data + (ulong)index * 4 > uint.MaxValue) return false;

        uint slot = data + index * 4;
        uint entry = ReadUInt32(slot);

        // Diagnostics record an actual address only after rechecking its name. Logs do not establish later readiness.
        if (!MatchesNameSnapshot(Process.ReadMemory, value, expected) ||
            ReadUInt32(value + 0x2C) != index || ReadUInt32(GNames) != data || ReadUInt32(slot) != entry)
            return false;

        reportedNames.Add(expected);
        log($"NATIVE_NAME_READ object=0x{value:X8} index=0x{index:X8} entry=0x{entry:X8} target={expected}");

        return true;
    }

    /// <summary>
    /// Reads an actual UObject's FName without calling name constructors or writing to the initializing
    /// native name array.
    /// </summary>
    internal static bool MatchesNameSnapshot(Func<uint, int, byte[]> readMemory, uint value, string expected)
    {
        if (value < 0x10000 || value > uint.MaxValue - 0x34 || expected.Length is < 1 or > 1023)
            return false;

        byte[] identity = readMemory(value + 0x2C, 8);
        uint index = BinaryPrimitives.ReadUInt32LittleEndian(identity);
        byte[] table = readMemory(GNames, 12);
        uint data = BinaryPrimitives.ReadUInt32LittleEndian(table);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4));
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8));

        if (BinaryPrimitives.ReadUInt32LittleEndian(identity.AsSpan(4)) != 0 || index == 0 || index >= count ||
            count > capacity || capacity > 1048576 || data < 0x10000 || (data & 3) != 0 ||
            (ulong)data + (ulong)capacity * 4 > uint.MaxValue)
            return false;

        uint slot = data + index * 4;
        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(readMemory(slot, 4));

        // The native name pool allocates 16 + (length + 1) * width bytes contiguously without padding. Valid entries and UTF-16 text can be unaligned.
        if (entry < 0x10000 ||
            (ulong)entry + 16 + (ulong)(expected.Length + 1) * 2 > uint.MaxValue)
            return false;

        uint encodedIndex = BinaryPrimitives.ReadUInt32LittleEndian(readMemory(entry + 8, 4));
        int width = (encodedIndex & 1) == 0 ? 1 : 2;

        if ((encodedIndex >> 1) != index)
            return false;

        // FNameEntry's low bit selects ANSI/UTF-16; text begins at +10. Read characters with bounds so short names cannot cross page boundaries.
        // Original comparisons use _stricmp/_wcsicmp. Fold only A–Z for these three ASCII targets; do not perform Unicode normalization.
        for (int position = 0; position <= expected.Length; position++)
        {
            byte[] unit = readMemory(entry + 16 + (uint)(position * width), width);
            uint character = width == 1 ? unit[0] : BinaryPrimitives.ReadUInt16LittleEndian(unit);
            uint expectedCharacter = position == expected.Length ? 0u : expected[position];

            if (character is >= 'A' and <= 'Z') character += 'a' - 'A';
            if (expectedCharacter is >= 'A' and <= 'Z') expectedCharacter += 'a' - 'A';
            if (character != expectedCharacter)
                return false;
        }

        // Reject mixed snapshots during name-array growth/reallocation or object renaming, and reread on the next probe.
        return readMemory(value + 0x2C, 8).AsSpan().SequenceEqual(identity) &&
            readMemory(GNames, 12).AsSpan().SequenceEqual(table) &&
            BinaryPrimitives.ReadUInt32LittleEndian(readMemory(slot, 4)) == entry &&
            BinaryPrimitives.ReadUInt32LittleEndian(readMemory(entry + 8, 4)) == encodedIndex;
    }

    internal uint Outermost(uint value)
    {
        var seen = new HashSet<uint>();

        for (int depth = 0; value != 0 && depth < 32; depth++)
        {
            Require(value >= 0x10000 && (value & 3) == 0 && seen.Add(value), "UObject Outer 链无效。");

            uint outer = ReadUInt32(value + 0x28);

            if (outer == 0) return value;
            value = outer;
        }

        throw new InvalidDataException("UObject Outer 链缺失或超过有界深度。");
    }

    internal uint ReadUInt32(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Process.ReadMemory(address, 4));
    internal static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    /// <summary>
    /// Rejects a resource-loading fatal branch already entered by this process. The host must recheck
    /// before publishing readiness.
    /// </summary>
    public void VerifyNoNativeFailure() => faultGuard?.ThrowIfFaulted();

    internal void ReportWaiting(string condition)
    {
        if (WaitingCondition != condition) log("NATIVE_WAIT " + condition);

        WaitingCondition = condition;
        CaptureReadinessDiagnostic(firstWaitingCaptured ? "waiting" : "first-rejection", force: !firstWaitingCaptured);
        firstWaitingCaptured = true;
    }

    internal async Task RekeyAsync(uint socket, CancellationToken cancellationToken)
    {
        uint page = Process.AllocateMemory(4096);
        var code = new X86Code();

        // Native initialization already set the socket crypto flag to 1. Do not write it or fabricate a socket that does not yet exist.
        Require(ReadUInt32(socket) == 0x01CD4210 && ReadUInt32(socket + 0x18) == 1,
            "live socket 的 encrypted 类型或 crypto flag 不匹配。");
        Process.WriteMemory(page, new byte[16]);
        code.Byte(0x68).UInt32(128).Byte(0x68).UInt32(page).Byte(0x68).UInt32(socket + 0x1C);
        code.Byte(0xB8).UInt32(0x004039C0).Hex("FFD083C40C");

        uint result = await InvokeMainThreadAsync(code.ToArray(), cancellationToken);

        Require(result == 0 && Hash(Process.ReadMemory(socket + 0x1C, 0x204)) == ZeroScheduleSha256,
            "live socket AES_SetKey 未生成已核实的完整 zero-key 加密/解密 schedule。");
        log($"ZERO_KEY_REKEY socket=0x{socket:X8} schedule_sha256={ZeroScheduleSha256}");
    }

    private void InstallLoadMapHooks()
    {
        marker = Process.AllocateMemory(4096);

        for (int index = 0; index < Stages.Length; index++)
        {
            var stage = Stages[index];
            uint trampoline = marker + 0x100 + (uint)index * 0x40;
            var code = new X86Code();

            code.Store(marker, (uint)index + 1).Byte(0x9C).Hex("830D").UInt32(marker + 4).Byte((byte)(1 << index));
            code.Byte(0x9D).Hex(stage.Original).Jump(trampoline, stage.Address + 5);
            Install(trampoline, code.ToArray());
            Install(stage.Address, new X86Code().Jump(stage.Address, trampoline).ToArray());
        }
    }

    private void InstallSetGameInfoHook()
    {
        trace = Process.AllocateMemory(4096);

        uint trampoline = trace + 0x100;
        var code = new X86Code();

        // Preserve original registers, flags, and stack layout while recording the actual UWorld* used by the subsequent tail call.
        code.Hex("9C50890D").UInt32(trace + 4).Hex("FF05").UInt32(trace).Hex("589D");
        code.Jump(trampoline, 0x00DA2760);
        Install(trampoline, code.ToArray());
        Install(0x009C9627, new X86Code().Call(0x009C9627, trampoline).ToArray());
    }

    private async Task<uint> InvokeMainThreadAsync(byte[] body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(!invocationFaulted, "前次原生调用未正常完成，必须先回收本次 Job，不能在同一进程继续注入。");

        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        uint previous = Process.SuspendMainThread();
        bool suspended = true;
        bool redirected = false;

        try
        {
            Require(previous == 0, "游戏主线程已经被其它操作挂起，拒绝覆盖上下文。");
            VerifyNoNativeFailure();

            var context = Process.GetMainThreadContext();
            var code = new X86Code();

            // Reuse the private call page only after returning to original code. If paused in the previous epilogue, retain another page for this call.
            if (invocationPage == 0 || (context.Eip >= invocationPage && context.Eip < invocationPage + 4096))
                invocationPage = Process.AllocateMemory(4096);

            uint page = invocationPage;
            uint done = page + 0x400;
            uint result = page + 0x404;
            uint fxState = page + 0x800;

            Process.WriteMemory(done, new byte[8]);
            // The interruption point may have a full x87 stack. Save and clear the temporary stack while preserving the control word to avoid callee stack overflow.
            // Page alignment meets FXSAVE's 16-byte requirement. Restore complete x87/XMM/MXCSR state and the original interrupted floating-point stack before return.
            // The interruption point may be in an STD reverse-copy region. Ordinary C++ calls require DF=0; final POPFD restores the original DF.
            code.Hex("9C60FC0FAE05").UInt32(fxState).Hex("DBE3D92D").UInt32(fxState);
            code.Bytes(body).Byte(0xA3).UInt32(result);
            code.Hex("0FAE0D").UInt32(fxState).Store(done, 1).Hex("619D68").UInt32(context.Eip).Byte(0xC3);
            Process.WriteMemory(page, code.ToArray());

            uint interruptedEip = context.Eip;

            context.Eip = page;
            Process.SetMainThreadContext(context);
            redirected = true;
            invocationCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            Process.ResumeMainThread();
            suspended = false;

            try
            {
                while (true)
                {
                    invocationCancellation.Token.ThrowIfCancellationRequested();

                    if (ReadUInt32(done) != 0) break;

                    await WaitForProgressAsync(invocationCancellation.Token);
                }
            }
            catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"AFDEV 主线程同步调用超过 5 秒：{WaitingCondition}；" +
                    $"interrupted_eip=0x{interruptedEip:X8}; invocation_page=0x{page:X8}。必须回收本次 Job。", failure);
            }

            return ReadUInt32(result);
        }
        catch
        {
            if (redirected) invocationFaulted = true;

            throw;
        }
        finally
        {
            if (suspended && Process.IsAlive) Process.ResumeMainThread();
        }
    }

    private async Task WaitForProgressAsync(CancellationToken cancellationToken)
    {
        if (!Process.IsAlive) throw new IOException($"AFDEV 在 {WaitingCondition} 期间退出。");

        CaptureReadinessDiagnostic("initializing");
        VerifyNoNativeFailure();
        await Task.Delay(50, cancellationToken);
        VerifyNoNativeFailure();
    }

    private void Verify(uint address, byte[] expected) => Require(Process.ReadMemory(address, expected.Length).AsSpan().SequenceEqual(expected),
        $"AFDEV 原始/已安装字节不匹配：0x{address:X8}，拒绝未知构建或运行期修改。");

    private void VerifyKnownCode()
    {
        // The first validator owns full PE-boundary and v4 checks. Normalize known date patches to the complete audited image first; all other code stays strictly checked.
        byte[] image = File.ReadAllBytes(Workspace.ExecutablePath);
        int pe = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C)));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        int table = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        bool textVerified = false;
        bool importsVerified = false;

        Require(Hash(image) == Validation.Manifest.AfdevSha256, "实例 EXE 在原生校验期间发生改变。");
        verifiedDateTimePatch = NormalizeAuditedDateTimePatch(image);

        for (int index = 0; index < count; index++)
        {
            int position = table + index * 40;

            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position + 12));
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position + 16)));
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(position + 20)));

            if (image.AsSpan(position, 8).SequenceEqual(".idata\0\0"u8))
            {
                Require(rva == 0x15F6000 && size == 0x5A6400 && offset == 0x15F5000 &&
                    Hash(image.AsSpan(offset, size).ToArray()) == "61f22b22a788df8b6b4123daa94c679ee4cb8636ba1f2a918f3391e49ecc9481",
                    "AFDEV 原始导入/虚表节摘要不匹配，拒绝未知 getsockname IAT 或对象布局。");
                importsVerified = true;
            }

            if (!image.AsSpan(position, 8).SequenceEqual(".text\0\0\0"u8)) continue;

            Require(rva == 0x1000 && size == 0x15F4C00 && offset == 0x400, "AFDEV 原始 .text 布局不属于已核实构建。");

            byte[] code = image.AsSpan(offset, size).ToArray();

            // The date variant passed whole-image validation. Normalize only the entry JMP that already passed full v4 validation here.
            Convert.FromHexString("C22800CCCC").CopyTo(code, checked((int)(ServerMove - 0x00400000 - rva)));
            Require(Hash(code) == "258420afa8e5287e0978b1935403f4133e0c2cac1497f0c47b9bccc3d87687f1",
                "AFDEV 原始代码节摘要不匹配，拒绝未知构建。");

            textVerified = true;
        }

        Require(textVerified && importsVerified, "AFDEV 缺少已核实的原始代码/导入节。");
        VerifyDateTimeCode();

        if (verifiedDateTimePatch)
            log("NATIVE_DATETIME_COMPATIBILITY 原有静态日期修复已核验，规范化完整映像与既有审计基线一致，保留原生补丁。");
    }

    private void Install(uint address, byte[] code)
    {
        Process.WriteMemory(address, code);
        Verify(address, code);
        installed.Add((address, code));
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    /// <summary>
    /// Assembles only x86 instructions audited in this file, calculating every rel32 from the actual remote
    /// address.
    /// </summary>
    private sealed class X86Code
    {
        private readonly List<byte> bytes = [];

        public X86Code Byte(byte value) { bytes.Add(value); return this; }
        public X86Code Bytes(byte[] value) { bytes.AddRange(value); return this; }
        public X86Code Hex(string value) => Bytes(Convert.FromHexString(value));
        public X86Code UInt32(uint value) => Bytes(BitConverter.GetBytes(value));
        public X86Code Store(uint address, uint value) => Hex("C705").UInt32(address).UInt32(value);
        public X86Code Jump(uint start, uint target) => Branch(start, target, 0xE9);
        public X86Code Call(uint start, uint target) => Branch(start, target, 0xE8);
        public byte[] ToArray() => bytes.ToArray();

        private X86Code Branch(uint start, uint target, byte opcode)
        {
            uint displacement = unchecked(target - (start + (uint)bytes.Count + 5));

            return Byte(opcode).UInt32(displacement);
        }
    }
}
