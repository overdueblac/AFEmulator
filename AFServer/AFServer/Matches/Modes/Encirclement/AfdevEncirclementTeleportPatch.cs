using System.Buffers.Binary;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Synchronizes owner position after Encirclement teleport. Retains native Pawn.OnTeleport target
/// selection, collision checks, and success branches, notifying PVE players only after success. The patch
/// exists only in this exclusive LD process, leaving shared client resources unchanged.
/// </summary>
internal static class AfdevEncirclementTeleportPatch
{
    internal const int NotificationOffset = 657;

    // Decoded from current Engine.u executable script. Verify eight-byte object references and FNames by complete paths/names.
    internal static readonly AfdevPveRetryScripts.Layout Original = new(
        "Engine.Pawn.OnTeleport",
        "1900000000000000000020000000000000000000001C00000000000000000000000000000000001F44657374696E6174696F6E00160F0000000000000000002507160182960000000000000000003600000000000000000016180D00720000000000000000002A16160F0000000000000000002E0000000000000000100000000000000000000000000000000000000F0000000000000000002E000000000000000000000000000000000007080182770000000000000000002A16182200771900000000000000000009000000000000000000000100000000000000002A16160F000000000000000000190000000000000000000900000000000000000000010000000000000000A50000000000000000001606400007BD0282770000000000000000002A16184100190000000000000000002B000000000000000000001C000000000000000017385C1900000000000000000009000000000000000000000100000000000000001616079402610B385C190000000000000000000900000000000000000000010000000000000000161B0000000000000000282716076202190000000000000000000A000000000000000000002D010000000000000000612B19000000000000000000090000000000000000000001000000000000000016076202770100000000000000002A16190100000000000000002100000000000000000000612B190000000000000000000900000000000000000000010000000000000000161901000000000000000029000000000000000000001B00000000000000001900000000000000000009000000000000000000000100000000000000004A16079102770100000000000000002A16190100000000000000000B000000000000000000001B00000000000000002A1606BA02E8A81F556E61626C6520746F2074656C65706F727420746F003856000000000000000000161606F90207F902720000000000000000002A16E81F556E61626C6520746F2074656C65706F7274202D206E6F2064657374696E6174696F6E20676976656E0016040B53",
        [
            (2, "Engine.Pawn.OnTeleport.Action"), (12, ""), (22, "Engine.SequenceOp.GetObjectVars"),
            (31, "Engine.Pawn.OnTeleport.objVars"), (55, "Engine.Pawn.OnTeleport.Idx"), (70, "Engine.Pawn.OnTeleport.Idx"),
            (80, "Engine.Pawn.OnTeleport.objVars"), (94, "Engine.Pawn.OnTeleport.destActor"), (107, "Engine.Pawn.OnTeleport.destActor"),
            (116, "Engine.Actor"), (126, "Engine.Pawn.OnTeleport.Idx"), (135, "Engine.Pawn.OnTeleport.objVars"),
            (145, "Engine.Pawn.OnTeleport.C"), (154, "Engine.Controller"), (163, "Engine.Pawn.OnTeleport.destActor"),
            (177, "Engine.Pawn.OnTeleport.C"), (193, "Engine.Pawn.OnTeleport.C"), (203, "Engine.Controller.Pawn"),
            (213, "Engine.Controller.Pawn"), (226, "Engine.Pawn.OnTeleport.destActor"), (236, "Engine.Pawn.OnTeleport.C"),
            (246, "Engine.Controller.Pawn"), (256, "Engine.Controller.Pawn"), (266, "Engine.Pawn.OnTeleport.Idx"),
            (284, "Engine.Pawn.OnTeleport.destActor"), (299, "Engine.Pawn.OnTeleport.Action"), (309, "Engine.SeqAct_Teleport.ShouldTeleport.ReturnValue"),
            (319, "Engine.SeqAct_Teleport.ShouldTeleport"), (332, "Engine.Pawn.OnTeleport.destActor"), (342, "Engine.Actor.Location"),
            (352, "Engine.Actor.Location"), (371, "Engine.Pawn.OnTeleport.destActor"), (381, "Engine.Actor.Location"),
            (391, "Engine.Actor.Location"), (417, "Engine.Pawn.OnTeleport.Action"), (427, "Engine.SeqAct_Teleport.bUpdateRotation"),
            (438, "Engine.SeqAct_Teleport.bUpdateRotation"), (450, "Engine.Pawn.OnTeleport.destActor"), (460, "Engine.Actor.Rotation"),
            (470, "Engine.Actor.Rotation"), (484, "Engine.Pawn.Controller"), (496, "Engine.Pawn.Controller"),
            (506, "Engine.Actor.SetRotation.ReturnValue"), (519, "Engine.Pawn.OnTeleport.destActor"), (529, "Engine.Actor.Rotation"),
            (539, "Engine.Actor.Rotation"), (550, "Engine.Pawn.Controller"), (560, ""),
            (580, "Engine.Pawn.OnTeleport.destActor"), (590, "Engine.Actor.Rotation"), (600, "Engine.Actor.Rotation"),
            (615, "Engine.Pawn.Controller"), (627, "Engine.Pawn.Controller"), (637, ""),
            (688, "Engine.Pawn.OnTeleport.destActor"), (706, "Engine.Pawn.OnTeleport.destActor")
        ],
        [(401, "PlayTeleportEffect", 0u), (570, "ClientSetRotation", 0u), (647, "OnTeleport", 0u)],
        [65, 172, 276, 279, 363, 413, 480, 611, 658, 699, 702]);

    /// <summary>
    /// The caller must suspend this no-human world's main thread and install before publishing player
    /// admission.
    /// </summary>
    internal static void Install(AfdevOwnedProcess process, AfdevNativeEvidence world)
    {
        var reader = new QueryInspector(process);

        if (!AfdevMapCatalog.MatchesEncirclement(world) ||
            world.ProcessId != process.ProcessId || world.ProcessCreationTimeUtc != process.CreationTimeUtc ||
            reader.U32(AfdevNativeBootstrap.GWorld) != world.World ||
            reader.U32(world.WorldInfo + 0x414) != world.GameInfo ||
            reader.U32(world.NetDriver + 0x48) != 0)
            throw new InvalidDataException("围剿战传送修复必须安装在本次尚未准入的 LD 世界。");

        var program = new AfdevPveRetryProgram(process);
        byte[] original = program.Verify(Original);
        uint clientSetLocation = program.Find("Engine.Controller.ClientSetLocation");

        // Preserve the native reliable client RPC. Ordinary property replication or local coordinate writes cannot replace owner synchronization.
        if (reader.U32(clientSetLocation + 0xAC) != 0x010201C2 ||
            reader.U16(clientSetLocation + 0xB0) != 0 ||
            reader.U16(clientSetLocation + 0xBE) != 24 ||
            reader.U32(clientSetLocation + 0xC8) != 0x0048DB10)
            throw new InvalidDataException("围剿战传送位置同步的原生可靠 RPC 布局不同。");

        byte[] controller = program.Variable("Engine.Pawn.Controller");
        byte[] player = [0x2E, .. program.Pointer(program.Find("PVEGame.PVEPlayerController")), .. controller];
        byte[] condition = [0x77, .. player, 0x2A, 0x16];
        byte[] location = program.Context([0x17], program.Variable("Engine.Actor.Location"), program.Find("Engine.Actor.Location"));
        byte[] rotation = program.Context(controller, program.Variable("Engine.Actor.Rotation"), program.Find("Engine.Actor.Rotation"));
        byte[] notification = program.Context(controller,
            program.Final("Engine.Controller.ClientSetLocation", location, rotation));
        byte[] guarded = [0x07, 0, 0, .. condition, .. notification];

        // SetLocation succeeded and native rotation/Controller.OnTeleport completed. Failure/no-target branches never reach here.
        BinaryPrimitives.WriteUInt16LittleEndian(guarded.AsSpan(1),
            checked((ushort)(NotificationOffset + guarded.Length)));
        program.Install(Original.Path, InsertNotification(original, guarded));
    }

    /// <summary>
    /// Inserts synchronization at complete statement boundaries, relocating absolute branches while
    /// preserving loops, context lengths, and all native instructions.
    /// </summary>
    internal static byte[] InsertNotification(byte[] original, byte[] notification)
    {
        if (original.Length != Original.Hex.Length / 2 || notification.Length == 0 ||
            original.Length + notification.Length >= ushort.MaxValue)
            throw new InvalidDataException("围剿战传送脚本长度超出已核验范围。");

        byte[] replacement = [.. original.AsSpan(0, NotificationOffset), .. notification,
            .. original.AsSpan(NotificationOffset)];

        foreach (int offset in Original.Jumps)
        {
            ushort target = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(offset));
            int relocatedOffset = offset >= NotificationOffset ? offset + notification.Length : offset;

            // Original branches to success-block end must pass the new player-type check first. Shift the failure block and later exits together.
            BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(relocatedOffset),
                checked((ushort)(target > NotificationOffset ? target + notification.Length : target)));
        }

        return replacement;
    }
}

