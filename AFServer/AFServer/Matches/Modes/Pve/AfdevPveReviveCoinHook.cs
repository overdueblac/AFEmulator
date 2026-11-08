using System.Buffers.Binary;
using System.Text;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Fixed AFDEV functional-item 14/0 bridge. The native game thread publishes monotonic ordinals and waits
/// for control-plane persistence before success. Other items retain the original entry. Code/mailbox are
/// reclaimed with this exclusive process and cannot transfer to another round.
/// </summary>
internal static class AfdevPveReviveCoinHook
{
    internal const uint Query = 0x01519870;
    internal const uint Consume = 0x01519950;
    internal const int SlotSize = 128;
    internal const uint Request = 8;
    internal const uint Completed = 12;
    internal const uint Result = 16;
    internal const uint Operation = 20;
    internal static readonly byte[] QueryOriginal = Convert.FromHexString("8B44240450");
    internal static readonly byte[] ConsumeOriginal = Convert.FromHexString("B828CB0000");

    internal sealed record Program(byte[] Code, byte[] Data, uint QueryEntry, uint ConsumeEntry);

    internal static Program Build(uint codeAddress, uint dataAddress, uint onlineDs, IReadOnlyList<MatchStartMember> members,
        uint queryContinuation = 0, uint consumeContinuation = 0, uint retryPage = 0)
    {
        if (members.Count is 0 or > 8 || members.Any(member => member.Observer || member.Profile.ReviveCoinCount < 0) ||
            members.Select(member => member.Profile.Uin).Distinct().Count() != members.Count)
            throw new InvalidDataException("PVE 复活币必须绑定一至八名唯一真人及非负初始数量。");

        byte[] data = new byte[members.Count * SlotSize];
        var a = new Assembler(codeAddress);
        uint queryEntry = codeAddress;

        void Entry(bool consume)
        {
            string prefix = consume ? "consume" : "query";

            a.Hex("558BEC53565751"); // Save nonvolatile registers and original this. Read parameters through EBP throughout.
            a.Hex("81F9"); a.Word(onlineDs); a.Jcc(0x85, prefix + "_original");
            if (!consume && retryPage != 0)
            {
                a.Hex("8B450C2D"); a.Word(AfdevPveRoundRetryRuntime.CommandBase);
                a.Hex("83F805"); a.Jcc(0x87, "query_coin");
                // Only validated scripts from this match may issue internal commands. Strings are borrowed during synchronous calls; the control plane processes them before responding.
                a.Hex("BB"); a.Word(retryPage);
                a.Hex("8943088B451089430C8B4508894310FF03");
                a.Hex("8B33FF15"); a.Word(0x019F62EC); a.Hex("89C7");
                a.Label("retry_wait"); a.Hex("397304"); a.Jcc(0x84, "retry_answered");
                a.Hex("6A01FF15"); a.Word(0x019F61BC); a.Hex("FF15"); a.Word(0x019F62EC);
                a.Hex("29F83D983A0000"); a.Jcc(0x82, "retry_wait"); a.Hex("0F0B");
                a.Label("retry_answered"); a.Hex("8B4318595F5E5B5DC20C00");
                a.Label("query_coin");
            }
            a.Hex("837D0C0E"); a.Jcc(0x85, prefix + "_original");
            a.Hex("837D1000"); a.Jcc(0x85, prefix + "_original");

            if (consume)
            {
                a.Hex("837D1401"); a.Jcc(0x85, prefix + "_no");
            }

            for (int index = 0; index < members.Count; index++)
            {
                uint slot = dataAddress + (uint)(index * SlotSize);
                string next = prefix + "_next_" + index;
                string uin = members[index].Profile.Uin.ToString(System.Globalization.CultureInfo.InvariantCulture);

                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index * SlotSize + 4), members[index].Profile.ReviveCoinCount);
                Encoding.Unicode.GetBytes(uin + '\0').CopyTo(data, index * SlotSize + 32);
                a.Hex("8B450885C0"); a.Jcc(0x84, prefix + "_no");
                a.Hex("817804"); a.Word((uint)uin.Length + 1); a.Jcc(0x85, next);
                a.Hex("817808"); a.Word((uint)uin.Length + 1); a.Jcc(0x82, prefix + "_no");
                a.Hex("8B3085F6"); a.Jcc(0x84, prefix + "_no");
                a.Hex("BF"); a.Word(slot + 32);
                a.Hex("B9"); a.Word((uint)uin.Length + 1);
                a.Hex("FCF366A7"); a.Jcc(0x85, next);
                a.Hex("BB"); a.Word(slot);
                a.Jump(prefix + "_matched");
                a.Label(next);
            }

            a.Jump(prefix + "_no");
            a.Label(prefix + "_matched");

            {
                // The same game thread calls synchronously without parallel mailbox reuse. Write operation before request ordinal, and result contents before completion ordinal.
                // Requests originate from verified native calls. Do not require an unmodified client to carry server ordinals first.
                // Game-thread serialization plus increment-and-wait prevents concurrent mailbox overwrites.
                a.Hex("C74314"); a.Word(consume ? 1u : 0u); a.Hex("FF4308");
                a.Hex("8B7308FF15"); a.Word(0x019F62EC); // GetTickCount IAT from the fixed image validated by the host.
                a.Hex("89C7");
                a.Label(prefix + "_wait");
                a.Hex("39730C"); a.Jcc(0x84, prefix + "_answered");
                a.Hex("6A01FF15"); a.Word(0x019F61BC); // Sleep(1), avoiding a busy loop that blocks control-plane work.
                a.Hex("FF15"); a.Word(0x019F62EC);
                a.Hex("29F83D983A0000"); a.Jcc(0x82, prefix + "_wait");
                // Unknown commit outcomes must not become fabricated failure followed by continued use of the same world. Health supervision reclaims this owned process.
                a.Hex("0F0B");
                a.Label(prefix + "_answered");
                a.Hex("8B431085C0"); a.Jcc(0x88, prefix + "_no");
                if (consume) a.Hex("B801000000");
                a.Jump(prefix + "_return");
            }

            a.Label(prefix + "_no"); a.Hex("31C0");
            a.Label(prefix + "_return"); a.Hex(consume ? "595F5E5B5DC21000" : "595F5E5B5DC20C00");
            a.Label(prefix + "_original"); a.Hex("595F5E5B5D");

            uint continuation = consume ? consumeContinuation : queryContinuation;

            // Non-revival-coin calls continue through this host's existing BIO isolation chain. Original instructions run only when isolation is absent.
            if (continuation != 0)
                a.JumpAddress(continuation);
            else
            {
                a.Hex(Convert.ToHexString(consume ? ConsumeOriginal : QueryOriginal));
                a.JumpAddress((consume ? Consume : Query) + 5);
            }
        }

        Entry(false);

        uint consumeEntry = codeAddress + (uint)a.Count;

        Entry(true);

        return new(a.Finish(), data, queryEntry, consumeEntry);
    }

    internal static byte[] Jump(uint from, uint to)
    {
        byte[] result = [0xE9, 0, 0, 0, 0];

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1), unchecked(to - from - 5));

        return result;
    }

    private sealed class Assembler(uint address)
    {
        private readonly List<byte> bytes = [];
        private readonly Dictionary<string, int> labels = [];
        private readonly List<(int Offset, string Label)> branches = [];
        internal int Count => bytes.Count;
        internal void Hex(string hex) => bytes.AddRange(Convert.FromHexString(hex));
        internal void Word(uint value)
        {
            byte[] raw = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
            bytes.AddRange(raw);
        }
        internal void Label(string label) => labels.Add(label, Count);
        internal void Jump(string label) { Hex("E9"); Branch(label); }
        internal void Jcc(byte condition, string label) { Hex("0F" + condition.ToString("X2")); Branch(label); }
        private void Branch(string label) { branches.Add((Count, label)); Word(0); }
        internal void JumpAddress(uint target) { Hex("E9"); Word(unchecked(target - address - (uint)Count - 4)); }
        internal byte[] Finish()
        {
            byte[] result = bytes.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset), labels[branch.Label] - branch.Offset - 4);

            return result;
        }
    }
}
