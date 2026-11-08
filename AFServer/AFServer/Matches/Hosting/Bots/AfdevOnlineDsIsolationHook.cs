using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Isolates internal BIO identity reporting in the fixed AFDEV build. Skip only the exact bot's own report
/// entry; humans still run the original entry. The current Job retains pages/trampolines until process
/// exit. Never revoke registration before UObject destruction callbacks.
/// </summary>
internal static class AfdevOnlineDsIsolationHook
{
    public const uint Active = 0;
    public const uint Fault = 4;
    public const int PageSize = 4096;
    public const uint OnlineDsVtable = 0x01DB2168;
    public const int CodeSize = 8192;
    // The identity proof set occupies at most [0x400,0xA00); diagnostic counters must not overwrite any object there.
    public const uint OriginalCounter = 0xA00;
    public const uint UnregisteredCounter = 0xA24;
    public const uint Sequence = 0xA48;
    public const uint LastDecision = 0xA4C;
    public const uint ConfirmRequest = 0xA50;
    public const uint ConfirmStarted = 0xA54;
    public const uint ConfirmCompleted = 0xA58;
    public const uint ConfirmStatus = 0xA5C;
    public const uint ConfirmIdleAck = 0xA60;
    private const uint Counter = 8;

    internal sealed record Entry(uint Number, string Name, uint Address, uint FilteredOffset);
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new(1, "SyncCamp", 0x0150D8A0, Counter),
        new(2, "DropOut", 0x01509800, Counter + 4),
        new(3, "SkillScore", 0x0150F140, Counter + 8),
        new(4, "BioGameOverPri", 0x0146AAF1, Counter + 12),
        new(5, "DropOutParticipantPri", 0x01509974, Counter + 16),
        new(6, "PingPri", 0x0150F963, Counter + 20),
        new(7, "GetFunctionPropCount", 0x01519870, 0xC0),
        new(8, "ConsumeFunctionProp", 0x01519950, 0xC4),
        new(9, "SetPlayerCamp", 0x015197E0, 0xC8)
    ];

    internal sealed record Patch(uint Address, byte[] Original, byte[] Replacement);
    internal sealed record Program(byte[] Code, IReadOnlyList<Patch> Patches, uint ConfirmationEntry);
    internal sealed record RosterProgram(byte[] Code, IReadOnlyList<Patch> Patches, IReadOnlyList<uint> ConfirmationEntries);

    /// <summary>
    /// Each bot's full identity page has an independent predicate and confirmation entry; the nine native
    /// sites install only the initial dispatcher. Unmatched calls pass original registers/arguments to the
    /// next slot; only the last restores the original function. Filter counts belong to matching slots,
    /// original-entry counts to the final slot. Passing other slots does not prove an external report was
    /// sent.
    /// </summary>
    public static RosterProgram Build(uint codeAddress, IReadOnlyList<uint> pageAddresses)
    {
        if (pageAddresses.Count is 0 or > 31 || pageAddresses.Distinct().Count() != pageAddresses.Count ||
            pageAddresses.Any(page => page < 0x10000 || (page & 3) != 0))
            throw new ArgumentException("OnlineDS 名册必须由最多 31 个独立身份页组成。", nameof(pageAddresses));

        var programs = new Program[pageAddresses.Count];
        byte[] code = new byte[checked(CodeSize * pageAddresses.Count)];

        for (int index = programs.Length - 1; index >= 0; index--)
        {
            Program? next = index + 1 < programs.Length ? programs[index + 1] : null;
            Dictionary<uint, uint>? nextEntries = next?.Patches.ToDictionary(patch => patch.Address,
                patch => unchecked(patch.Address + 5 + BinaryPrimitives.ReadUInt32LittleEndian(patch.Replacement.AsSpan(1, 4))));

            programs[index] = BuildCore(checked(codeAddress + (uint)(index * CodeSize)), pageAddresses[index], nextEntries, pageAddresses);
            programs[index].Code.CopyTo(code, index * CodeSize);
        }

        return new RosterProgram(code, programs[0].Patches, programs.Select(program => program.ConfirmationEntry).ToArray());
    }

    /// <summary>
    /// Builds a nonrepeatable descriptor after the identity transaction and before skill/team calls. Write
    /// the complete page before publishing Active=2. Only an empty UIN is allowed during transition; the
    /// game thread raises Active to 1 after seeing the frozen UIN. Check Fault after every native command;
    /// any exact-identity break terminates the owned instance.
    /// </summary>
    public static byte[] Describe(AfdevOwnedProcess process, AfdevNativeEvidence world,
        uint controller, Guid botId, string uin)
    {
        var r = new QueryInspector(process);
        byte[] page = new byte[PageSize];

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset), value);
        uint Field(uint instance, string path, string kind = "ObjectProperty", uint size = 4) =>
            r.FieldAddress(instance, path.Split('.')[^1], path, kind, size);

        if (botId == Guid.Empty || uin != "bot-" + botId.ToString("N")[..12] ||
            world.InstanceId == Guid.Empty || world.AttemptId == Guid.Empty || world.AllocationId == Guid.Empty ||
            world.ProcessId != process.ProcessId || world.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            r.U32(0x02066BF8) != world.World)
            throw new InvalidDataException("OnlineDS 隔离描述与本次世界、进程或完整 BotId 不一致。");

        uint pri = r.U32(Field(controller, "Engine.Controller.PlayerReplicationInfo"));
        uint online = r.U32(Field(world.GameInfo, "UTGame.TGGame.OnlineDs"));

        if (r.Path(r.Identity(controller).Class) != "TGBioGame.TGBioPlayerController" ||
            r.Path(r.Identity(pri).Class) != "TGBioGame.TGBioPlayerReplicationInfo" ||
            r.Path(r.Identity(online).Class) != "UTGame.TGOnlineDS" || r.U32(online) != OnlineDsVtable)
            throw new InvalidDataException("OnlineDS 隔离只支持当前普通 BIO 对象与已审核的实际 OnlineDS 类。");

        Put(0x40, world.World);
        Put(0x44, world.WorldInfo);
        Put(0x48, world.GameInfo);
        Put(0x4C, controller);
        Put(0x50, pri);
        Put(0x54, online);
        Put(0x60, Field(controller, "Engine.PlayerController.Player") - controller);
        Put(0x64, Field(controller, "Engine.Controller.PlayerReplicationInfo") - controller);
        Put(0x68, Field(controller, "Engine.Actor.WorldInfo") - controller);
        Put(0x6C, Field(pri, "Engine.Actor.Owner") - pri);
        Put(0x70, Field(pri, "Engine.Actor.WorldInfo") - pri);
        Put(0x74, Field(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12) - pri);
        Put(0x78, (uint)uin.Length + 1);
        Put(0x80, r.U32(0x01FE22C0));
        Put(0x84, Field(world.WorldInfo, "Engine.WorldInfo.Game") - world.WorldInfo);
        Put(0x88, Field(world.GameInfo, "UTGame.TGGame.OnlineDs") - world.GameInfo);
        Put(0x8C, r.Identity(r.U32(0x02063820)).Address);
        Encoding.Unicode.GetBytes(uin + '\0').CopyTo(page, 0x200);
        world.InstanceId.TryWriteBytes(page.AsSpan(0x100, 16));
        world.AttemptId.TryWriteBytes(page.AsSpan(0x110, 16));
        world.AllocationId.TryWriteBytes(page.AsSpan(0x120, 16));
        botId.TryWriteBytes(page.AsSpan(0x130, 16));
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(0x140), world.WorldGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(0x148), process.CreationTimeUtc.UtcTicks);

        QueryObject[] ids = r.IdentityClosure(world.World, world.WorldInfo, world.GameInfo, controller, pri, online).ToArray();

        if (ids.Length == 0 || ids.Length > 64)
            throw new InvalidDataException("OnlineDS 身份闭包超出固定描述页容量。");

        Put(0x7C, (uint)ids.Length);

        for (int index = 0; index < ids.Length; index++)
        {
            QueryObject id = ids[index];
            uint[] values = [id.Address, id.Index, id.Outer, id.NameIndex, id.NameNumber, id.Class];

            for (int field = 0; field < values.Length; field++)
                Put(0x400 + index * 24 + field * 4, values[field]);
        }

        return page;
    }

    /// <summary>
    /// Calls confirmation before Tick with integer/floating-point state saved. The activation gate still
    /// performs the final composition.
    /// </summary>
    public static byte[] AppendConfirmation(byte[] wrapper, uint entry)
    {
        byte[] anchor = Convert.FromHexString("0FAE0C2489DC619DFF742408FF742408E8");
        int insertion = wrapper.AsSpan().IndexOf(anchor);

        if (entry < 0x10000 || insertion < 0 || wrapper.AsSpan(insertion + 1).IndexOf(anchor) >= 0)
            throw new InvalidDataException("OnlineDS 确认门缺少唯一 Tick 保存边界或代码地址。");

        byte[] call = [0xB8, 0, 0, 0, 0, 0xFF, 0xD0];

        BinaryPrimitives.WriteUInt32LittleEndian(call.AsSpan(1, 4), entry);

        byte[] result = [.. wrapper.AsSpan(0, insertion), .. call, .. wrapper.AsSpan(insertion)];
        int displacement = insertion + call.Length + anchor.Length;
        int original = BinaryPrimitives.ReadInt32LittleEndian(wrapper.AsSpan(insertion + anchor.Length, 4));

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(displacement, 4), checked(original - call.Length));
        return result;
    }

    /// <summary>
    /// Installs only before the main thread's first resume. The host jointly validates original bytes and
    /// the full PE hash.
    /// </summary>
    public static Program Build(uint codeAddress, uint pageAddress) => BuildCore(codeAddress, pageAddress, null, [pageAddress]);

    private static Program BuildCore(uint codeAddress, uint pageAddress, IReadOnlyDictionary<uint, uint>? nextEntries,
        IReadOnlyList<uint> rosterPages)
    {
        var a = new Assembler(codeAddress);
        var entries = new List<(uint Address, byte[] Original, int Offset)>();

        uint C(uint offset) => checked(pageAddress + offset);
        void Load(uint address) { a.Hex("A1"); a.Word(address); }
        void Eq(uint address, string failure) { a.Hex("3B05"); a.Word(address); a.Jcc(0x85, failure); }
        void Increment(uint offset) { a.Hex("FF05"); a.Word(C(offset)); }
        void Decision(uint value) { a.Hex("C705"); a.Word(C(LastDecision)); a.Word(value); }
        void Start(uint address, string original)
        {
            entries.Add((address, Convert.FromHexString(original), a.Offset));
            a.Hex("9C60");
            // Active isolation allows page writes only from the same game thread. An odd version means evaluation is incomplete; the host rereads at most three times.
            Increment(Sequence);
        }
        void Predicate(bool text, string original, uint entry)
        {
            a.Hex("C705"); a.Word(C(0x24)); a.Word(entry);
            a.Hex("8B4C2418890D"); a.Word(C(0x28));
            a.Hex(text ? "BA01000000" : "31D2");
            a.Call("predicate");
            a.Hex("85C0"); a.Jcc(0x84, original);
        }
        void Filtered(uint offset)
        {
            Increment(offset);
            Decision(1);
            Increment(Sequence);
        }
        void Original(uint entry)
        {
            // Count only after choosing the original entry. Count unregistered calls separately; they are not a human control sample.
            Increment(OriginalCounter + (entry - 1) * 4);
            foreach (uint page in rosterPages)
            {
                a.Hex("833D"); a.Word(page + Active); a.Hex("00"); a.Jcc(0x85, $"original_registered_{entry}");
            }
            Increment(UnregisteredCounter + (entry - 1) * 4);
            Decision(3); a.Jump($"original_complete_{entry}");
            a.Label($"original_registered_{entry}"); Decision(2);
            a.Label($"original_complete_{entry}"); Increment(Sequence);
        }
        void Continue(string label, string original, uint address, uint entry)
        {
            a.Label(label);

            if (Forward(entry))
                return;

            Original(entry); a.Hex("619D"); a.Hex(original); a.JumpAddress(address);
        }
        bool Forward(uint entry)
        {
            if (nextEntries == null)
                return false;

            Decision(0);
            Increment(Sequence);
            a.Hex("619D");
            a.JumpAddress(nextEntries[Entries.Single(item => item.Number == entry).Address]);
            return true;
        }

        Start(0x0150D8A0, "B824CB0000");
        a.Hex("8B442428"); Predicate(true, "camp_original", 1);
        Filtered(Counter); a.Hex("619D31C0C20800");
        Continue("camp_original", "B824CB0000", 0x0150D8A5, 1);

        Start(0x01509800, "6AFF680E849901");
        a.Hex("8B44242C"); Predicate(false, "drop_original", 2);
        Filtered(Counter + 4); a.Hex("619DC20800");
        Continue("drop_original", "6AFF680E849901", 0x01509807, 2);

        Start(0x0150F140, "6AFF6838889901");
        a.Hex("8D442428"); Predicate(true, "skill_original", 3);
        Filtered(Counter + 8); a.Hex("619D8D4C2404");
        a.CallAddress(0x004838F0); // The original body owns a by-value FString; execute the same engine destructor.
        a.Hex("C22C00");
        Continue("skill_original", "6AFF6838889901", 0x0150F147, 3);

        Start(0x0146AAF1, "85F60F84E50E0000");
        a.Hex("8B442404"); Predicate(false, "report_original", 4);
        Filtered(Counter + 12); a.Hex("619D"); a.JumpAddress(0x0146B9DE);
        a.Label("report_original");

        if (!Forward(4))
        {
            Original(4); a.Hex("619D85F6"); a.JccAddress(0x84, 0x0146B9DE);
            a.JumpAddress(0x0146AAF9);
        }

        Start(0x01509974, "3BF574158B86C0030000");
        a.Hex("8B442404"); Predicate(false, "count_original", 5);
        Filtered(Counter + 16); a.Hex("619D"); a.JumpAddress(0x0150998D);
        a.Label("count_original");

        if (!Forward(5))
        {
            Original(5); a.Hex("619D3BF5"); a.JccAddress(0x84, 0x0150998D);
            a.Hex("8B86C0030000"); a.JumpAddress(0x0150997E);
        }

        Start(0x0150F963, "85F60F841C010000");
        a.Hex("8B442404"); Predicate(false, "ping_original", 6);
        Filtered(Counter + 20); a.Hex("619D"); a.JumpAddress(0x0150FA87);
        a.Label("ping_original");

        if (!Forward(6))
        {
            Original(6); a.Hex("619D85F6"); a.JccAddress(0x84, 0x0150FA87);
            a.JumpAddress(0x0150F96B);
        }

        Start(0x01519870, "8B44240450");
        a.Hex("8B442428"); Predicate(true, "prop_original", 7);
        Filtered(0xC0); a.Hex("619D31C0C20C00");
        Continue("prop_original", "8B44240450", 0x01519875, 7);

        Start(0x01519950, "B828CB0000");
        a.Hex("8B442428"); Predicate(true, "consume_original", 8);
        Filtered(0xC4); a.Hex("619D31C0C21000");
        Continue("consume_original", "B828CB0000", 0x01519955, 8);

        Start(0x015197E0, "A120D4F901");
        a.Hex("8B442428"); Predicate(true, "set_camp_original", 9);
        Filtered(0xC8); a.Hex("619D31C0C20800");
        Continue("set_camp_original", "A120D4F901", 0x015197E5, 9);

        // EAX is PRI or FString*, EDX the parameter type. Read-only checks run after the caller saves every integer register and EFLAGS.
        a.Label("predicate");
        a.Hex("A3"); a.Word(C(0x2C));
        a.Hex("8915"); a.Word(C(0x30));
        a.Hex("813D"); a.Word(C(Active)); a.Word(0); a.Jcc(0x84, "no");
        a.Hex("BD0A000000813D"); a.Word(C(Active)); a.Word(2); a.Jcc(0x87, "invalid");
        a.Hex("BD01000000");
        Load(0x02066BF8); Eq(C(0x40), "invalid");
        a.Hex("BD02000000");
        Load(C(0x80)); a.Hex("643B0524000000"); a.Jcc(0x85, "invalid");
        a.Hex("BD03000000");
        Load(C(0x54)); a.Hex("81386821DB01"); a.Jcc(0x85, "invalid");
        a.Hex("BD04000000");
        Load(C(0x7C)); a.Hex("85C0"); a.Jcc(0x84, "invalid");
        a.Hex("83F840"); a.Jcc(0x87, "invalid");
        a.Hex("89C7BE"); a.Word(C(0x400));
        a.Label("ids");
        a.Hex("8B068B56043B15"); a.Word(0x02053EAC); a.Jcc(0x83, "invalid");
        a.Hex("8B0D"); a.Word(0x02053EA8);
        a.Hex("85C9"); a.Jcc(0x84, "invalid");
        a.Hex("390491"); a.Jcc(0x85, "invalid");
        foreach (var field in new[] { (4, 4), (0x28, 8), (0x2C, 12), (0x30, 16), (0x34, 20) })
        {
            a.Hex("8B50" + field.Item1.ToString("X2") + "3B56" + field.Item2.ToString("X2"));
            a.Jcc(0x85, "invalid");
        }

        a.Hex("83C6184F"); a.Jcc(0x85, "ids");
        a.Hex("BD05000000");
        foreach (var relation in new[] { (0x4C, 0x64, 0x50), (0x4C, 0x68, 0x44), (0x50, 0x6C, 0x4C), (0x50, 0x70, 0x44) })
        {
            Load(C((uint)relation.Item1));
            a.Hex("8B0D"); a.Word(C((uint)relation.Item2));
            a.Hex("8B0408"); Eq(C((uint)relation.Item3), "invalid");
        }

        a.Hex("BD08000000");
        foreach (var relation in new[] { (0x44, 0x84, 0x48), (0x48, 0x88, 0x54) })
        {
            Load(C((uint)relation.Item1));
            a.Hex("8B0D"); a.Word(C((uint)relation.Item2));
            a.Hex("8B0408"); Eq(C((uint)relation.Item3), "invalid");
        }

        a.Hex("BD06000000");
        Load(C(0x4C)); a.Hex("8B0D"); a.Word(C(0x60));
        a.Hex("833C0800"); a.Jcc(0x85, "invalid");
        a.Hex("BD07000000");
        Load(C(0x50)); a.Hex("0305"); a.Word(C(0x74));
        a.Call("text_equal"); a.Hex("85C0"); a.Jcc(0x85, "uin_ready");
        a.Hex("813D"); a.Word(C(Active)); a.Word(2); a.Jcc(0x85, "invalid");
        Load(C(0x50)); a.Hex("0305"); a.Word(C(0x74));
        a.Hex("83780400"); a.Jcc(0x84, "uin_empty");
        a.Hex("83780401"); a.Jcc(0x85, "invalid");
        a.Hex("83780801"); a.Jcc(0x82, "invalid");
        a.Hex("8B0085C0"); a.Jcc(0x84, "invalid");
        a.Hex("66833800"); a.Jcc(0x85, "invalid");
        a.Label("uin_empty");
        a.Hex("BD09000000833D"); a.Word(C(0x30)); a.Hex("00"); a.Jcc(0x84, "empty_pri_input");
        // Before a new slot's ChangeUin, strings for other exact identities continue to the next slot. If this slot's own
        // frozen UIN appears in a call while PRI remains empty, still reject it; transition must not become unconditional acceptance.
        Load(C(0x2C)); a.Call("text_equal"); a.Hex("85C0"); a.Jcc(0x85, "invalid"); a.Jump("no");
        a.Label("empty_pri_input");
        Load(C(0x2C)); Eq(C(0x50), "no"); a.Jump("matched");
        a.Label("uin_ready");
        a.Hex("C705"); a.Word(C(Active)); a.Word(1);
        Load(C(0x2C));
        a.Hex("833D"); a.Word(C(0x30)); a.Hex("00"); a.Jcc(0x85, "input_text");
        Eq(C(0x50), "no"); a.Jump("matched");
        a.Label("input_text");
        a.Call("text_equal"); a.Hex("85C0"); a.Jcc(0x84, "no");
        a.Label("matched");
        a.Hex("BD03000000");
        Load(C(0x24)); a.Hex("83F803"); a.Jcc(0x86, "check_this");
        a.Hex("83F806"); a.Jcc(0x86, "yes");
        a.Label("check_this");
        Load(C(0x28)); Eq(C(0x54), "invalid");
        a.Label("yes");
        a.Hex("B801000000C3");
        a.Label("invalid");
        a.Hex("892D"); a.Word(C(Fault));
        Decision(4);
        Increment(Sequence);
        // After a full input match, invalid identity prevents external reporting. Do not guess the current object; write fault code/entry to the page before terminating the owned instance.
        a.Hex("0F0B");
        a.Label("no"); a.Hex("31C0C3");

        a.Label("text_equal");
        a.Hex("85C0"); a.Jcc(0x84, "no");
        a.Hex("8B48043B0D"); a.Word(C(0x78)); a.Jcc(0x85, "no");
        a.Hex("85C9"); a.Jcc(0x84, "no");
        a.Hex("83F921"); a.Jcc(0x87, "no");
        a.Hex("3B4808"); a.Jcc(0x87, "no");
        a.Hex("8B0085C0"); a.Jcc(0x84, "no");
        a.Hex("BA"); a.Word(C(0x200));
        a.Label("characters");
        a.Hex("668B18663B1A"); a.Jcc(0x85, "no");
        a.Hex("83C00283C20249"); a.Jcc(0x85, "characters");
        a.Hex("B801000000C3");

        int confirmationOffset = a.Offset;

        // This entry confirms only frozen identity. It uses no account interface to trigger predicates and increments none of the nine reporting counters.
        a.Hex("9C60833D"); a.Word(C(ConfirmRequest)); a.Hex("00"); a.Jcc(0x84, "confirm_idle");
        foreach (uint address in new uint[] { 0x01FE229C, 0x01FD69E0, 0x02053E50, 0x02053E58 })
        {
            a.Hex("833D"); a.Word(address); a.Hex("00"); a.Jcc(0x85, "confirm_done");
        }

        Increment(Sequence);
        a.Hex("C705"); a.Word(C(0x24)); a.Word(10);
        Decision(0);
        // The host may revoke a request between delayed GC completion and entry. Confirm cancellation if execution has not started.
        a.Hex("833D"); a.Word(C(ConfirmRequest)); a.Hex("00"); a.Jcc(0x84, "confirm_cancelled");
        a.Hex("BD0A000000833D"); a.Word(C(ConfirmRequest)); a.Hex("01"); a.Jcc(0x85, "invalid");
        foreach (uint offset in new[] { ConfirmStarted, ConfirmCompleted, ConfirmStatus, Fault })
        {
            a.Hex("833D"); a.Word(C(offset)); a.Hex("00"); a.Jcc(0x85, "invalid");
        }

        a.Hex("BD02000000833D"); a.Word(0x01FE22C4); a.Hex("01"); a.Jcc(0x85, "invalid");
        Load(0x01FE22C0); Eq(C(0x80), "invalid");
        a.Hex("BD01000000");
        Load(0x02063820); Eq(C(0x8C), "invalid");
        a.Hex("85C0"); a.Jcc(0x84, "invalid");
        a.Hex("83B87806000000"); a.Jcc(0x85, "confirm_deferred");
        a.Hex("C705"); a.Word(C(ConfirmStarted)); a.Word(1);
        Load(C(0x54)); a.Hex("A3"); a.Word(C(0x28));
        Load(C(0x50)); a.Hex("31D2"); a.Call("predicate");
        a.Hex("BD0A00000085C0"); a.Jcc(0x84, "invalid");
        // Normal PRI filtering permits empty UIN with Active=2; explicit confirmation requires the complete final UIN actually read.
        a.Hex("BD07000000833D"); a.Word(C(Active)); a.Hex("01"); a.Jcc(0x85, "invalid");
        a.Hex("C705"); a.Word(C(ConfirmStatus)); a.Word(1);
        a.Hex("C705"); a.Word(C(ConfirmCompleted)); a.Word(1);
        a.Hex("C705"); a.Word(C(ConfirmRequest)); a.Word(0);
        Decision(5);
        a.Label("confirm_deferred"); Increment(Sequence); a.Jump("confirm_done");
        a.Label("confirm_cancelled"); Increment(Sequence);
        // With Active=0, the complete descriptor is still being published/read back by the host; idle entry points must not write it.
        a.Label("confirm_idle"); a.Hex("833D"); a.Word(C(Active)); a.Hex("00"); a.Jcc(0x84, "confirm_done");
        a.Hex("F0FF05"); a.Word(C(ConfirmIdleAck));
        a.Label("confirm_done"); a.Hex("619DC3");

        byte[] code = a.Finish();
        Patch[] patches = entries.Select(e =>
        {
            byte[] replacement = Enumerable.Repeat((byte)0x90, e.Original.Length).ToArray();

            replacement[0] = 0xE9;
            BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(1), unchecked(codeAddress + (uint)e.Offset - e.Address - 5));
            return new Patch(e.Address, e.Original, replacement);
        }).ToArray();

        if (code.Length > CodeSize)
            throw new InvalidDataException("OnlineDS 跳板超出保留页容量。");

        return new Program(code, patches, checked(codeAddress + (uint)confirmationOffset));
    }

    private sealed class Assembler(uint address)
    {
        private readonly List<byte> code = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];
        public int Offset => code.Count;
        public void Hex(string value) => code.AddRange(Convert.FromHexString(value));
        public void Word(uint value)
        {
            byte[] data = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            code.AddRange(data);
        }
        public void Label(string name) => labels.Add(name, code.Count);
        public void Call(string name) { Hex("E8"); Branch(name); }
        public void Jump(string name) { Hex("E9"); Branch(name); }
        public void Jcc(byte condition, string name) { Hex("0F" + condition.ToString("X2")); Branch(name); }
        private void Branch(string name) { branches.Add((code.Count, name)); Word(0); }
        public void JumpAddress(uint target) { Hex("E9"); Relative(target); }
        public void CallAddress(uint target) { Hex("E8"); Relative(target); }
        public void JccAddress(byte condition, uint target) { Hex("0F" + condition.ToString("X2")); Relative(target); }
        private void Relative(uint target) => Word(unchecked(target - address - (uint)code.Count - 4));
        public byte[] Finish()
        {
            byte[] result = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset), labels[branch.Label] - branch.Offset - 4);

            return result;
        }
    }
}
