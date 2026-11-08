namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private FirstPawnEvidence? firstPawnEvidence;

    /// <summary>
    /// When the full roster first yields a complete native Pawn binding. Read-only observation requires
    /// neither human visibility nor hits.
    /// </summary>
    public DateTimeOffset? FirstPawnReadyAtUtc
        => Volatile.Read(ref (rosterOwner ?? this).firstPawnEvidence)?.ObservedAtUtc;

    private void ObserveFirstPawn(QueryInspector reader)
    {
        AfdevBioBotRuntime owner = rosterOwner ?? this;

        if (Volatile.Read(ref owner.firstPawnEvidence) != null || preparedBot == null || world == null)
            return;

        uint pawnField = reader.FieldAddress(preparedBot.Controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);
        uint pawn = reader.U32(pawnField);

        if (pawn < 0x10000)
            return;

        QueryObject identity;

        try
        {
            identity = reader.Identity(pawn);

            // Native spawning may bind both ends within one frame. Wait through incomplete state without intervening in spawning or writing missing properties.
            if (reader.U32(reader.FieldAddress(pawn, "Controller", "Engine.Pawn.Controller", "ObjectProperty", 4)) != preparedBot.Controller ||
                reader.U32(reader.FieldAddress(pawn, "PlayerReplicationInfo", "Engine.Pawn.PlayerReplicationInfo", "ObjectProperty", 4)) != preparedBot.Pri ||
                reader.U32(reader.FieldAddress(pawn, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4)) != world.WorldInfo ||
                unchecked((int)reader.U32(reader.FieldAddress(pawn, "Health", "Engine.Pawn.Health", "IntProperty", 4))) <= 0 ||
                process.ReadMemory(reader.FieldAddress(pawn, "Role", "Engine.Actor.Role", "ByteProperty", 1), 1)[0] != 3 ||
                (reader.U32(pawn + 0x0C) & 0x20000000) != 0 || (reader.U32(pawn + 0xB0) & 8) != 0 ||
                reader.U32(pawnField) != pawn || reader.Identity(pawn) != identity)
                return;
        }
        catch (Exception) when (process.IsAlive && reader.U32(pawnField) != pawn)
        {
            // Read races during normal Pawn replacement are not preparation failures; supervision samples again next time.
            return;
        }

        var observed = new FirstPawnEvidence(preparedBot.BotId, preparedBot.Controller, preparedBot.Pri,
            identity, DateTimeOffset.UtcNow);

        if (Interlocked.CompareExchange(ref owner.firstPawnEvidence, observed, null) != null)
            return;

        owner.SaveRoster("first-pawn-ready", new ObserveFirstPawnJson1
        {
            InstanceId = world.InstanceId, AttemptId = world.AttemptId, AllocationId = world.AllocationId, WorldGeneration = world.WorldGeneration,
            PawnReady = true, FirstPawn = observed, ClientVisibilityValidated = false
        });
        log($"BIO_BOT_PAWN_READY bot={preparedBot.BotId:N} controller=0x{preparedBot.Controller:X8} pawn=0x{pawn:X8} observed_utc={observed.ObservedAtUtc:O}");
    }

    private sealed record FirstPawnEvidence(Guid BotId, uint Controller, uint Pri, QueryObject Pawn, DateTimeOffset ObservedAtUtc);
}
