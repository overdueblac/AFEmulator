using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
internal static class QueryHook
{
    public const uint ActorProcessEvent = 0x0075A9F0;
}

internal static class QueryRun
{
    public static QueryObservation Read(AfdevOwnedProcess process, uint command, uint parms)
    {
        byte[] bytes = process.ReadMemory(command, 0x70);

        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        return new QueryObservation(DateTimeOffset.UtcNow, U(0), U(4), U(8), U(12), U(16), U(20), U(24), U(28), U(32), U(36), U(40), U(44), unchecked((int)U(48)), U(56), U(60), U(104), U(108), BinaryPrimitives.ReadInt32LittleEndian(process.ReadMemory(parms, 4)));
    }
}

sealed record QueryObservation(DateTimeOffset Time, uint Requested, uint Started, uint Complete, uint Status, uint LastGuard, uint BlockedTicks, uint ThreadBefore, uint ThreadAfter, uint WorldBefore, uint WorldAfter, uint GameBefore, uint GameAfter, int Result, uint ClassBefore, uint ClassAfter, uint StackBefore, uint StackAfter, int ParmsValue);
