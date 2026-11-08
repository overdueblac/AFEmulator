using System.Buffers.Binary;
using AFServer.AssaultFire.Game;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Revival-coin mailbox for the current exclusive world, handling frozen humans only. Stop cancels
/// transaction work before the host closes process handles.
/// </summary>
internal sealed class AfdevPveReviveCoinRuntime
{
    private readonly AfdevOwnedProcess process;
    private readonly MatchStartSnapshot snapshot;
    private readonly MatchStartMember[] members;
    private readonly IPveReviveCoinStore store;
    private readonly Action<string> log;
    private readonly uint page;
    private readonly AfdevPveRoundRetryRuntime roundRetry;

    internal AfdevPveReviveCoinRuntime(AfdevOwnedProcess process, MatchStartSnapshot snapshot,
        AfdevNativeEvidence evidence, IPveReviveCoinStore store, Action<string> log, AfdevBioBotRuntime? botRuntime = null)
    {
        this.process = process;
        this.snapshot = snapshot;
        this.store = store;
        this.log = log;
        members = snapshot.Members.Where(member => !member.Observer).ToArray();

        var reader = new QueryInspector(process);
        uint online = reader.U32(reader.FieldAddress(evidence.GameInfo, "OnlineDs", "UTGame.TGGame.OnlineDs", "ObjectProperty", 4));

        if (snapshot.AllocationId != evidence.AllocationId || !PveReviveCoins.Supports(snapshot.Descriptor.ModeId) ||
            reader.Path(reader.Identity(online).Class) != "UTGame.TGOnlineDS")
            throw new InvalidDataException("PVE 复活币本次世界身份不匹配。");

        var query = ResolveEntrypoint(AfdevPveReviveCoinHook.Query, AfdevPveReviveCoinHook.QueryOriginal);
        var consume = ResolveEntrypoint(AfdevPveReviveCoinHook.Consume, AfdevPveReviveCoinHook.ConsumeOriginal);
        var wipeRevive = new AfdevPveWipeRevivePatch(process, evidence);
        roundRetry = new AfdevPveRoundRetryRuntime(process, evidence, online, log);

        page = process.AllocateMemory(4096);

        uint code = process.AllocateMemory(4096);
        var program = AfdevPveReviveCoinHook.Build(code, page, online, members, query.Continuation, consume.Continuation, roundRetry.Page);
        byte[] queryJump = AfdevPveReviveCoinHook.Jump(AfdevPveReviveCoinHook.Query, program.QueryEntry);
        byte[] consumeJump = AfdevPveReviveCoinHook.Jump(AfdevPveReviveCoinHook.Consume, program.ConsumeEntry);

        if (program.Code.Length > 4096) throw new InvalidDataException("复活币桥接代码超出独占页。");

        process.WriteMemory(page, program.Data);
        process.WriteMemory(code, program.Code);

        if (!process.ReadMemory(code, program.Code.Length).AsSpan().SequenceEqual(program.Code))
            throw new InvalidDataException("PVE 复活币桥接代码回读不一致。");

        // Install before publishing this round's endpoints. Suspend the main thread so it cannot execute a partially written JMP.
        uint previous = process.SuspendMainThread();

        try
        {
            if (previous != 0) throw new InvalidOperationException("PVE 复活币入口安装遇到已挂起线程。");

            if (!process.ReadMemory(AfdevPveReviveCoinHook.Query, 5).AsSpan().SequenceEqual(query.EntryBytes) ||
                !process.ReadMemory(AfdevPveReviveCoinHook.Consume, 5).AsSpan().SequenceEqual(consume.EntryBytes))
                throw new InvalidDataException("PVE 复活币入口在安装前已改变。");

            wipeRevive.Install();
            roundRetry.Install();
            process.WriteMemory(AfdevPveReviveCoinHook.Query, queryJump);
            process.WriteMemory(AfdevPveReviveCoinHook.Consume, consumeJump);

            if (!process.ReadMemory(AfdevPveReviveCoinHook.Query, 5).AsSpan().SequenceEqual(queryJump) ||
                !process.ReadMemory(AfdevPveReviveCoinHook.Consume, 5).AsSpan().SequenceEqual(consumeJump))
                throw new InvalidDataException("PVE 复活币入口安装回读不一致。");
        }
        finally { process.ResumeMainThread(); }

        log($"PVE_REVIVE_BOUND allocation={snapshot.AllocationId:N} members={members.Length} online_isolation={(botRuntime is null ? "absent" : "preserved")}");
        log($"PVE_WIPE_REVIVE_READY allocation={snapshot.AllocationId:N} pending_revive_prevents_wipe=true");
        log($"PVE_ROUND_RETRY_READY allocation={snapshot.AllocationId:N} shared_coin_receipts=true");

        (byte[] EntryBytes, uint Continuation) ResolveEntrypoint(uint address, byte[] original)
        {
            if (botRuntime is not null)
                return botRuntime.GetOnlineIsolationContinuation(process, address, original);
            if (!process.ReadMemory(address, original.Length).AsSpan().SequenceEqual(original))
                throw new InvalidDataException($"PVE 复活币原始入口 0x{address:X8} 不匹配。");

            return (original.ToArray(), 0);
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await roundRetry.ServiceAsync(snapshot, members, store, page, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log($"PVE_ROUND_RETRY_ERROR allocation={snapshot.AllocationId:N} kind={error.GetType().Name} detail={error.Message}");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                continue;
            }

            for (int index = 0; index < members.Length; index++)
            {
                uint slot = page + (uint)(index * AfdevPveReviveCoinHook.SlotSize);
                uint sequence = Read(slot + AfdevPveReviveCoinHook.Request);

                if (sequence == Read(slot + AfdevPveReviveCoinHook.Completed)) continue;

                try
                {
                    uint operation = Read(slot + AfdevPveReviveCoinHook.Operation);
                    log($"PVE_REVIVE_REQUEST allocation={snapshot.AllocationId:N} session={members[index].Session.SessionId:N} " +
                        $"sequence={sequence} operation={operation}");
                    int remaining = operation switch
                    {
                        0 => await store.GetCountAsync(snapshot, members[index], cancellationToken).ConfigureAwait(false),
                        1 => await store.ConsumeAsync(snapshot, members[index], sequence, cancellationToken).ConfigureAwait(false),
                        _ => throw new InvalidDataException("原生复活币邮箱操作无效。")
                    };

                    cancellationToken.ThrowIfCancellationRequested();
                    Write(slot + 4, (uint)Math.Max(0, remaining));
                    Write(slot + AfdevPveReviveCoinHook.Result, unchecked((uint)remaining));
                    Write(slot + AfdevPveReviveCoinHook.Completed, sequence);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Network failure may follow a successful MongoDB commit. Reread the receipt with the same ordinal; never allocate another ordinal and deduct twice.
                    log($"PVE_REVIVE_RETRY allocation={snapshot.AllocationId:N} session={members[index].Session.SessionId:N} " +
                        $"sequence={sequence} kind={error.GetType().Name}");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private uint Read(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(process.ReadMemory(address, 4));
    private void Write(uint address, uint value)
    {
        byte[] bytes = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        process.WriteMemory(address, bytes);
    }
}
