using System.Buffers.Binary;
using System.Security.Cryptography;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
/// <summary>
/// Game-thread safe-point wrapper for the fixed AFDEV image, preserving complete register and
/// floating-point state.
/// </summary>
sealed class TickHook
{
    public const uint CallSite = 0x009B972D;
    public const uint Callee = 0x00A8C040;
    public const string ImageHash = "14dbe9c8722b8e6433427aae689a4238cb1677a66174159bddb3b62e0fcb91bc";
    public const int TraceSize = 0x38;
    private static readonly byte[] OriginalCall = Convert.FromHexString("E80E290D00");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static byte[] Build(uint codeAddress, uint traceAddress)
    {
        var code = new List<byte>();

        void Hex(string value) => code.AddRange(Convert.FromHexString(value));
        void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        void Save() => Hex("9C6089E381EC2002000083E4F00FAE0424DBE3D92C24FC");
        void Restore() => Hex("0FAE0C2489DC619D");
        void Store(uint offset)
        {
            Hex("A3");
            Word(traceAddress + offset);
        }

        void Global(uint address, uint offset)
        {
            Hex("A1");
            Word(address);
            Store(offset);
        }

        void Increment(uint offset)
        {
            Hex("F0FF05");
            Word(traceAddress + offset);
        }

        Save();
        Hex("890D");
        Word(traceAddress + 8); // Original ECX, recorded without dereferencing UWorld.
        Hex("8B4328");
        Store(0x28); // Original tickType after pushfd + pushad.
        Hex("8B432C");
        Store(0x2C); // Original delta float bits.
        Hex("FF15");
        Word(0x019F6228);
        Store(0x10); // Verified GetCurrentThreadId import.
        Global(0x01FD69E0, 0x18);
        Global(0x01FE22C0, 0x20);
        Global(0x01FE22C4, 0x24);
        Increment(0);
        Restore();
        // Two identical [esp+8] reads copy delta then tickType. The original callee's ret 8 consumes the copies.
        Hex("FF742408FF742408E8");
        Word(unchecked(Callee - (codeAddress + (uint)code.Count + 4)));
        Save();
        Global(0x02066BF8, 0x0C);
        Hex("FF15");
        Word(0x019F6228);
        Store(0x14);
        Global(0x01FD69E0, 0x1C);
        Global(0x02053E50, 0x30);
        Global(0x02053E58, 0x34);
        Increment(4);
        Restore();
        Hex("C20800"); // The wrapper consumes the caller's original arguments and returns to the original continuation.
        return code.ToArray();
    }

    public static byte[] CallPatch(uint address)
    {
        byte[] bytes = new byte[5];

        bytes[0] = 0xE8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), unchecked(address - (CallSite + 5)));
        return bytes;
    }

    public static TickSample Read(AfdevOwnedProcess process, uint traceAddress)
    {
        byte[] bytes = process.ReadMemory(traceAddress, TraceSize);

        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        return new TickSample(DateTimeOffset.UtcNow, U32(0), U32(4), U32(8), U32(12), U32(16), U32(20), U32(24), U32(28), U32(32), U32(36), U32(40), U32(44), U32(48), U32(52));
    }
}

sealed record TickSample(DateTimeOffset ObservedUtc, uint Entered, uint Returned, uint WorldThisBefore, uint GWorldAfter, uint TidBefore, uint TidAfter, uint GcActiveBefore, uint GcActiveAfter, uint EngineGameThreadId, uint EngineThreadCheckEnabled, uint TickType, uint DeltaFloatBits, uint PurgePhase2053e50After, uint PurgePhase2053e58After);
