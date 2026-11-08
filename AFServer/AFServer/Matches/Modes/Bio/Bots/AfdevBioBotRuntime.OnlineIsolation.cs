using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private static readonly (int Offset, int Length)[] OnlineDescriptorRanges =
        [(0x40, 0x50), (0x100, 0x50), (0x200, 0x200), (0x400, 0x600)];
    private readonly object onlineObservationGate = new();
    private uint onlineIsolationPage;
    private uint onlineConfirmationEntry;
    private bool onlineIdentityPublished;
    private Guid onlineBotId;
    private string onlineUin = string.Empty;
    private uint onlineController;
    private int onlineConfirmationIssued;
    private AfdevOnlineDsIsolationSnapshot? onlineLastSavedSnapshot;
    private DateTimeOffset onlineLastSavedAt;
    private int onlineSnapshotsSaved;
    private IReadOnlyList<AfdevOnlineDsIsolationHook.Patch> onlineIsolationPatches = [];

    private void InstallOnlineIsolationSuspended()
    {
        uint code = process.AllocateMemory(AfdevOnlineDsIsolationHook.CodeSize * rosterSlots.Length);

        foreach (AfdevBioBotRuntime slot in rosterSlots)
        {
            slot.onlineIsolationPage = process.AllocateMemory(AfdevOnlineDsIsolationHook.PageSize, executable: false);
            process.WriteMemory(slot.onlineIsolationPage, new byte[AfdevOnlineDsIsolationHook.PageSize]);
        }

        uint[] pages = rosterSlots.Select(slot => slot.onlineIsolationPage).ToArray();
        AfdevOnlineDsIsolationHook.RosterProgram program = AfdevOnlineDsIsolationHook.Build(code, pages);

        foreach (AfdevOnlineDsIsolationHook.Patch patch in program.Patches)
        {
            if (!process.ReadMemory(patch.Address, patch.Original.Length).AsSpan().SequenceEqual(patch.Original))
                throw new InvalidDataException($"OnlineDS 隔离入口 0x{patch.Address:X8} 与当前已核验映像不符。");
        }

        process.WriteMemory(code, program.Code);

        if (!process.ReadMemory(code, program.Code.Length).AsSpan().SequenceEqual(program.Code))
            throw new InvalidDataException("OnlineDS 隔离跳板回读不一致。");

        foreach (AfdevOnlineDsIsolationHook.Patch patch in program.Patches)
        {
            process.WriteMemory(patch.Address, patch.Replacement);

            if (!process.ReadMemory(patch.Address, patch.Replacement.Length).AsSpan().SequenceEqual(patch.Replacement))
                throw new InvalidDataException("OnlineDS 隔离入口安装回读不一致。");
        }

        for (int index = 0; index < rosterSlots.Length; index++)
            rosterSlots[index].onlineConfirmationEntry = program.ConfirmationEntries[index];

        onlineIsolationPatches = program.Patches;
        SaveRoster("online-isolation-installed", new InstallOnlineIsolationSuspendedJson1
        {
            InstanceId = workspace.InstanceId, attemptId = attemptId, ProcessId = process.ProcessId, CreationTimeUtc = process.CreationTimeUtc,
            code = code, pages = pages, ConfirmationEntries = program.ConfirmationEntries, ProgramSha256 = TickHook.Hash(program.Code), Patches = program.Patches,
            Entries = AfdevOnlineDsIsolationHook.Entries,
            IdentityPublished = false, PagesRetainedUntilOwnedJobStops = true
        });
    }

    /// <summary>
    /// Gets the installed entry's subsequent dispatch address for composition within the same owned
    /// process. Accepts only a retained trampoline still matching live state; unknown JMPs and diagnostic
    /// files do not prove ownership. Pause the game thread and recheck returned bytes before replacing the
    /// entry. Retain the isolation page until whole-Job cleanup.
    /// </summary>
    internal (byte[] EntryBytes, uint Continuation) GetOnlineIsolationContinuation(AfdevOwnedProcess ownedProcess,
        uint address, ReadOnlySpan<byte> original)
    {
        if (!ReferenceEquals(process, ownedProcess) || rosterOwner != this)
            throw new InvalidOperationException("OnlineDS 后续分派必须来自同一自有进程的名册所有者。");

        var patch = onlineIsolationPatches.SingleOrDefault(item => item.Address == address);

        if (patch is null || !patch.Original.AsSpan().SequenceEqual(original) ||
            patch.Replacement.Length != 5 || patch.Replacement[0] != 0xE9 ||
            !process.ReadMemory(address, 5).AsSpan().SequenceEqual(patch.Replacement))
            throw new InvalidDataException($"OnlineDS 自有隔离入口 0x{address:X8} 已改变或没有安装记录。");

        uint continuation = unchecked(address + 5 + BinaryPrimitives.ReadUInt32LittleEndian(patch.Replacement.AsSpan(1)));

        return (patch.Replacement.ToArray(), continuation);
    }

    private void PublishOnlineIdentity(AfdevNativeEvidence boundWorld, uint controller, AfdevBioBotIdentity identity)
    {
        if (onlineIsolationPage == 0 || onlineIdentityPublished)
            throw new InvalidOperationException("OnlineDS 隔离页未安装或内部身份已发布，禁止重试。");

        byte[] descriptor = AfdevOnlineDsIsolationHook.Describe(process, boundWorld, controller, identity.BotId, identity.Uin);
        var reader = new QueryInspector(process);

        foreach (uint offset in new[] { AfdevOnlineDsIsolationHook.Active, AfdevOnlineDsIsolationHook.Fault,
            AfdevOnlineDsIsolationHook.ConfirmRequest, AfdevOnlineDsIsolationHook.ConfirmStarted,
            AfdevOnlineDsIsolationHook.ConfirmCompleted, AfdevOnlineDsIsolationHook.ConfirmStatus })
            if (reader.U32(onlineIsolationPage + offset) != 0)
                throw new InvalidDataException("OnlineDS 本槽已有身份或确认状态，禁止重新发布。");

        // The factory has returned but ChangeUin has not run. Publish exact PC/PRI identity before allowing profile or skill calls.
        // Empty slots also participate in shared routing. Native entries own ordinals/diagnostic counters; write only immutable descriptors without resetting concurrent fields.
        foreach ((int offset, int length) in OnlineDescriptorRanges)
        {
            byte[] section = descriptor.AsSpan(offset, length).ToArray();
            uint address = onlineIsolationPage + (uint)offset;

            process.WriteMemory(address, section);

            if (!process.ReadMemory(address, length).AsSpan().SequenceEqual(section))
                throw new InvalidDataException("OnlineDS 内部身份描述页回读不一致。");
        }

        process.WriteMemory(onlineIsolationPage + AfdevOnlineDsIsolationHook.Active, [2, 0, 0, 0]);
        onlineBotId = identity.BotId;
        onlineUin = identity.Uin;
        onlineController = controller;
        onlineIdentityPublished = true;
        Save("online-isolation-registered", new PublishOnlineIdentityJson1
        {
            InstanceId = boundWorld.InstanceId, AttemptId = boundWorld.AttemptId, AllocationId = boundWorld.AllocationId, WorldGeneration = boundWorld.WorldGeneration,
            BotId = identity.BotId, Uin = identity.Uin, controller = controller, Page = onlineIsolationPage
        });
    }

    private byte[] AppendOnlineIdentityConfirmation(byte[] wrapper)
    {
        if (onlineIsolationPage == 0 || onlineConfirmationEntry == 0)
            throw new InvalidOperationException("OnlineDS 确认入口尚未安装。");

        return AfdevOnlineDsIsolationHook.AppendConfirmation(wrapper, onlineConfirmationEntry);
    }

    private async Task ConfirmOnlineIdentityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workStopping.Token.ThrowIfCancellationRequested();
        VerifyOnlineIdentityForConfirmation();

        if (Interlocked.CompareExchange(ref onlineConfirmationIssued, 1, 0) != 0)
            throw new InvalidOperationException("本次 OnlineDS 身份确认已发布，禁止重发。");

        var reader = new QueryInspector(process);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        foreach (uint offset in new[] { AfdevOnlineDsIsolationHook.ConfirmRequest, AfdevOnlineDsIsolationHook.ConfirmStarted,
            AfdevOnlineDsIsolationHook.ConfirmCompleted, AfdevOnlineDsIsolationHook.ConfirmStatus })
            if (reader.U32(onlineIsolationPage + offset) != 0)
                throw new InvalidDataException("OnlineDS 确认页已有请求或完成状态，不能复用。");

        Save("online-identity-confirmation-request", CaptureOnlineIsolationSnapshot());
        process.WriteMemory(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmRequest, [1, 0, 0, 0]);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workStopping.Token.ThrowIfCancellationRequested();
            CheckBinding();

            AfdevOnlineDsIsolationSnapshot state = CaptureOnlineIsolationSnapshot();
            uint completed = state.ConfirmationCompleted;
            uint request = state.ConfirmationRequest;
            uint status = state.ConfirmationStatus;

            if (!state.IsCoherent)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                    throw new IOException("OnlineDS 身份确认未得到一致的完成快照，必须回收原 Job。");

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (completed == 1 && request == 0 && status == 1)
            {
                VerifyOnlineIdentityForConfirmation();

                if (state.Active != 1 || state.Fault != 0)
                    throw new InvalidDataException("游戏线程确认后 OnlineDS 身份状态不为 Active=1。");

                Save("online-identity-confirmed", state);
                return;
            }

            if (completed != 0 || status != 0 && status != 1 || request != 1 || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidDataException("OnlineDS 游戏线程身份确认没有已知完成结果，禁止重发；必须回收原 Job。");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private void VerifyOnlineIdentityForConfirmation()
    {
        CheckBinding();

        if (!onlineIdentityPublished || world == null || onlineController == 0 || onlineBotId == Guid.Empty)
            throw new InvalidOperationException("OnlineDS 尚无本次 Bot 的完整登记身份。");

        byte[] expected = AfdevOnlineDsIsolationHook.Describe(process, world, onlineController, onlineBotId, onlineUin);
        byte[] page = process.ReadMemory(onlineIsolationPage, AfdevOnlineDsIsolationHook.PageSize);
        var reader = new QueryInspector(process);

        uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(offset, 4));

        // Compare immutable descriptors only. Host rewrites of Active or page republication cannot replace game-thread confirmation.
        foreach ((int offset, int length) in OnlineDescriptorRanges)
            if (!page.AsSpan(offset, length).SequenceEqual(expected.AsSpan(offset, length)))
                throw new InvalidDataException("OnlineDS 确认前后的冻结身份或反射偏移已改变。");

        if (Read(0) is not (1 or 2) || Read(4) != 0 || reader.U32(Read(0x4C) + Read(0x60)) != 0)
            throw new InvalidDataException("OnlineDS 确认缺少有效隔离状态或无 Player 的内部身份。");

        foreach ((int instance, int offset, int target) in new[]
        {
            (0x4C, 0x64, 0x50), (0x4C, 0x68, 0x44), (0x50, 0x6C, 0x4C),
            (0x50, 0x70, 0x44), (0x44, 0x84, 0x48), (0x48, 0x88, 0x54)
        })
            if (reader.U32(Read(instance) + Read(offset)) != Read(target))
                throw new InvalidDataException("OnlineDS 确认的 PC/PRI/WorldInfo/Game 关系不符。");

        uint uinAddress = Read(0x50) + Read(0x74);
        uint data = reader.U32(uinAddress);
        uint count = reader.U32(uinAddress + 4);
        uint capacity = reader.U32(uinAddress + 8);

        if (data < 0x10000 || count != Read(0x78) || count is 0 or > 33 || capacity < count ||
            !process.ReadMemory(data, checked((int)count * 2)).AsSpan().SequenceEqual(page.AsSpan(0x200, checked((int)count * 2))))
            throw new InvalidDataException("OnlineDS 确认未读到完整最终内部 UIN。");
    }

    private async Task DisarmOnlineIdentityConfirmationAsync(CancellationToken cancellationToken)
    {
        if (onlineIsolationPage == 0 || !process.IsAlive)
            return;

        var reader = new QueryInspector(process);

        if (Volatile.Read(ref onlineConfirmationIssued) == 0 &&
            reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmRequest) == 0 &&
            reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmStarted) == 0)
            return;

        uint before = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmIdleAck);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        process.WriteMemory(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmRequest, [0, 0, 0, 0]);

        while (process.IsAlive && reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmIdleAck) == before)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("OnlineDS 确认入口未确认退出，必须回收原 Job。");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        if (!process.IsAlive)
            return;

        uint started = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmStarted);
        uint completed = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmCompleted);
        uint status = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmStatus);

        if (started > 1 || started == 1 && (completed != 1 || status != 1) ||
            started == 0 && (completed != 0 || status != 0))
            throw new IOException("OnlineDS 确认已经进入但完成未知，必须回收原 Job。");

        Save("online-identity-confirmation-stopped", new DisarmOnlineIdentityConfirmationAsyncJson1
        {
            BeforeIdleAck = before, AfterIdleAck = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.ConfirmIdleAck),
            Started = started, Completed = completed, Status = status, PagesRetainedUntilOwnedJobStops = true
        });
    }

    /// <summary>
    /// Reads cumulative filtered/pass-through counts at nine native entries, retrying at most three times
    /// around active writes. Counts accumulate from installation and are not reset on identity publication.
    /// Original-entry counts reside in the last preallocated slot; exact matching slots own filter counts.
    /// Mark incoherent snapshots explicitly. Pass-through proves entry into an original branch, not human
    /// identity, return, or external-report contents.
    /// </summary>
    public AfdevOnlineDsIsolationSnapshot CaptureOnlineIsolationSnapshot()
    {
        if (onlineIsolationPage == 0)
            throw new InvalidOperationException("Bot 原生操作缺少 OnlineDS 隔离页。");

        var reader = new QueryInspector(process);
        byte[] page = [];
        bool coherent = false;
        int attempts = 0;

        uint Read(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(checked((int)offset), 4));

        while (attempts < 3)
        {
            attempts++;

            uint before = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.Sequence);

            page = process.ReadMemory(onlineIsolationPage, AfdevOnlineDsIsolationHook.PageSize);

            uint after = reader.U32(onlineIsolationPage + AfdevOnlineDsIsolationHook.Sequence);

            if (before == after && Read(AfdevOnlineDsIsolationHook.Sequence) == after && (after & 1) == 0)
            {
                // Unregistered original entries need not share a thread; ordinary INC ordinals cannot establish coherence.
                coherent = Read(AfdevOnlineDsIsolationHook.Active) is 1 or 2 && Read(AfdevOnlineDsIsolationHook.Fault) == 0;
                break;
            }
        }

        uint count = Read(0x78);
        string uin = count is > 0 and <= 33
            ? Encoding.Unicode.GetString(page, 0x200, checked((int)(count - 1) * 2))
            : string.Empty;
        AfdevOnlineDsIsolationEntryCounters[] counters = AfdevOnlineDsIsolationHook.Entries.Select(entry =>
            new AfdevOnlineDsIsolationEntryCounters(entry.Number, entry.Name, entry.Address, Read(entry.FilteredOffset),
                Read(AfdevOnlineDsIsolationHook.OriginalCounter + (entry.Number - 1) * 4),
                Read(AfdevOnlineDsIsolationHook.UnregisteredCounter + (entry.Number - 1) * 4))).ToArray();
        uint entryNumber = Read(0x24);
        string entryName = entryNumber == 10 ? "IdentityConfirmation" :
            AfdevOnlineDsIsolationHook.Entries.FirstOrDefault(entry => entry.Number == entryNumber)?.Name ?? "None";

        return new AfdevOnlineDsIsolationSnapshot(DateTimeOffset.UtcNow, onlineIsolationPage, coherent, attempts,
            Read(AfdevOnlineDsIsolationHook.Sequence), Read(AfdevOnlineDsIsolationHook.Active), Read(AfdevOnlineDsIsolationHook.Fault),
            new Guid(page.AsSpan(0x100, 16)), new Guid(page.AsSpan(0x110, 16)), new Guid(page.AsSpan(0x120, 16)),
            new Guid(page.AsSpan(0x130, 16)), BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(0x140, 8)),
            Read(0x40), Read(0x4C), Read(0x50), uin, entryNumber, entryName, Read(0x28), Read(0x2C), Read(0x30) == 1,
            (AfdevOnlineDsIsolationDecision)Read(AfdevOnlineDsIsolationHook.LastDecision), counters,
            Read(AfdevOnlineDsIsolationHook.ConfirmRequest), Read(AfdevOnlineDsIsolationHook.ConfirmStarted),
            Read(AfdevOnlineDsIsolationHook.ConfirmCompleted), Read(AfdevOnlineDsIsolationHook.ConfirmStatus),
            Read(AfdevOnlineDsIsolationHook.ConfirmIdleAck));
    }

    private void CheckOnlineIsolation()
    {
        AfdevOnlineDsIsolationSnapshot snapshot = CaptureOnlineIsolationSnapshot();

        // Host health checks and slot behavior may read native state concurrently. The slot still serially owns evidence ordinals and file writes.
        lock (onlineObservationGate)
        {
            bool stateChanged = onlineLastSavedSnapshot == null || snapshot.Active != onlineLastSavedSnapshot.Active ||
                snapshot.Fault != onlineLastSavedSnapshot.Fault;
            bool countersChanged = onlineLastSavedSnapshot == null || !snapshot.Entries.SequenceEqual(onlineLastSavedSnapshot.Entries);
            bool newFault = snapshot.Fault != 0 && snapshot.Fault != onlineLastSavedSnapshot?.Fault;
            bool intervalElapsed = snapshot.CapturedAtUtc - onlineLastSavedAt >= TimeSpan.FromSeconds(1);

            // Automatically retain at most 32 normal changes plus one final failure. Explicit research samples are caller-owned, without per-frame files.
            if (newFault || snapshot.IsCoherent && onlineSnapshotsSaved < 32 &&
                (stateChanged || countersChanged && intervalElapsed))
            {
                Save($"online-isolation-observation-{++onlineSnapshotsSaved:D2}", snapshot);
                onlineLastSavedSnapshot = snapshot;
                onlineLastSavedAt = snapshot.CapturedAtUtc;
            }
        }

        if (snapshot.Fault != 0)
            throw new InvalidDataException($"OnlineDS 内部身份隔离失效，故障码 {snapshot.Fault}，入口 {snapshot.LastEntryName}；必须回收本次 Job。");
    }
}

/// <summary>
/// Latest completed native-trampoline decision. Writing Fault immediately terminates the current owned
/// instance.
/// </summary>
public enum AfdevOnlineDsIsolationDecision
{
    /// <summary>No completed entry yet.</summary>
    None = 0,
    /// <summary>The exact internal bot was filtered.</summary>
    Filtered = 1,
    /// <summary>Internal identity is published; current arguments enter the original function.</summary>
    RegisteredOriginal = 2,
    /// <summary>Internal identity is not published; current arguments enter the original function.</summary>
    UnregisteredOriginal = 3,
    /// <summary>Identity validation failed; the original entry was not allowed.</summary>
    Fault = 4,
    /// <summary>
    /// The game thread fully confirmed the final internal UIN. This confirmation counts toward none of the
    /// nine account entries.
    /// </summary>
    IdentityConfirmed = 5
}

/// <summary>
/// Cumulative native counts for one entry. UnregisteredPassThrough is a subset of OriginalPassThrough.
/// Their difference represents original branches during registration, without automatically treating nonbot
/// arguments as authorized humans.
/// </summary>
public sealed record AfdevOnlineDsIsolationEntryCounters(uint Entry, string Name, uint Address,
    uint Filtered, uint OriginalPassThrough, uint UnregisteredPassThrough)
{
    /// <summary>
    /// Cumulative original-branch entries while registered, using native 32-bit counter arithmetic.
    /// </summary>
    public uint RegisteredOriginalPassThrough => unchecked(OriginalPassThrough - UnregisteredPassThrough);
}

/// <summary>
/// Read-only isolation-page evidence. IsCoherent means only that this read did not straddle trampoline
/// writes. Other evidence from the same experiment must establish native returns, human eligibility, and
/// network-report contents.
/// </summary>
public sealed record AfdevOnlineDsIsolationSnapshot(DateTimeOffset CapturedAtUtc, uint Page, bool IsCoherent, int ReadAttempts,
    uint Sequence, uint Active, uint Fault, Guid InstanceId, Guid AttemptId, Guid AllocationId, Guid BotId, long WorldGeneration,
    uint World, uint Controller, uint Pri, string Uin, uint LastEntry, string LastEntryName, uint LastReceiver, uint LastInput,
    bool LastInputIsString, AfdevOnlineDsIsolationDecision LastDecision, IReadOnlyList<AfdevOnlineDsIsolationEntryCounters> Entries,
    uint ConfirmationRequest, uint ConfirmationStarted, uint ConfirmationCompleted, uint ConfirmationStatus, uint ConfirmationIdleAck);
