using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Freezes native pathfinding and object/coordinate approaches. References belong only to the current
/// world; external scripts are rejected.
/// </summary>
sealed class AfdevNavigationProgram
{
    public const string EvidenceId = "navigation-pve-pursuit-20261011";
    private static readonly IReadOnlyDictionary<uint, uint> Slots = new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>
    {
        [1] = 0x45FAB0, [15] = 0x479000, [29] = 0x460770, [30] = 0x460790, [32] = 0x4607B0,
        [40] = 0x45D110, [42] = 0x45D110, [8] = 0x45D050, [97] = 0x45D300, [98] = 0x45D330,
        [35] = 0x464290, [69] = 0x460270, [500] = 0x8EB4D0, [501] = 0x8EEA80,
        [502] = 0x8EB890, [503] = 0x8EEA90, [517] = 0x951220, [520] = 0x951620, [521] = 0x9515B0
    });
    private readonly byte[] program;
    private readonly QueryObject controllerIdentity;
    private readonly QueryObject? goalIdentity;
    private readonly uint processId;
    private readonly DateTimeOffset processCreationTime;
    private readonly uint world;
    private readonly uint worldInfo;

    private AfdevNavigationProgram(byte[] program, AfdevOwnedProcess process, AfdevNativeEvidence evidence,
        QueryObject controller, QueryObject? goal, uint[] references, AfdevBioBotNavigationMode mode,
        AfdevBioBotPoint? point)
    {
        this.program = (byte[])program.Clone();
        controllerIdentity = controller;
        goalIdentity = goal;
        processId = process.ProcessId;
        processCreationTime = process.CreationTimeUtc;
        world = evidence.World;
        worldInfo = evidence.WorldInfo;
        References = Array.AsReadOnly((uint[])references.Clone());
        Mode = mode;
        Point = point;
    }

    public AfdevBioBotNavigationMode Mode { get; }
    public string Name => Mode.ToString();
    public byte[] Program => (byte[])program.Clone();
    public uint StopOffset => checked((uint)program.Length - 1);
    public uint LatentIndex => Mode == AfdevBioBotNavigationMode.Point ? 501u : 503u;
    // An empty path can reach Stop synchronously. It is an observable query failure, not an invalid program or successful arrival.
    public bool RequireLatentObservation => false;
    public IReadOnlyDictionary<uint, uint> ExpectedNativeSlots => Slots;
    public IReadOnlyList<uint> References { get; }
    public uint Goal => goalIdentity?.Address ?? 0;
    public AfdevBioBotPoint? Point { get; }

    public static AfdevNavigationProgram Build(AfdevOwnedProcess process, AfdevNativeEvidence evidence,
        uint controller, uint goal, AfdevBioBotNavigationMode mode, AfdevBioBotPoint? point = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (point.HasValue != (mode is AfdevBioBotNavigationMode.Point or AfdevBioBotNavigationMode.PointReachability) ||
            point is { } position && (!position.IsValid || goal != 0))
            throw new ArgumentException("坐标动作必须提供有限地图坐标，不得同时提供 Actor 地址。");

        AfdevNavigationNativeImage.Verify(process);

        var reader = new QueryInspector(process, cacheMetadata: true);
        QueryObject controllerIdentity = reader.Identity(controller);
        QueryObject? goalIdentity = point.HasValue ? null : reader.Identity(goal);

        foreach (var slot in Slots)
        {
            if (reader.U32(0x01FF4D10 + slot.Key * 4) != slot.Value)
                throw new InvalidDataException("FindPath interpreter native slot differs: " + slot.Key);
        }

        if (reader.Path(controllerIdentity.Class) != "TGBioGame.TGBioPlayerController" ||
            (!point.HasValue && !reader.IsA(goal, "Engine.NavigationPoint") &&
                (mode == AfdevBioBotNavigationMode.FindPath || !reader.IsA(goal, "Engine.Pawn"))) ||
            mode == AfdevBioBotNavigationMode.Pursuit && !reader.IsA(goal, "Engine.Pawn"))
            throw new InvalidDataException("导航要求普通 BIO 控制器；FindPath 使用导航点，Pursuit 使用当前世界的 Pawn。");

        uint property = reader.FindField(controllerIdentity.Class, "MoveTarget", "Engine.Controller.MoveTarget");
        uint addressOfTarget = reader.FieldAddress(controller, "MoveTarget", "Engine.Controller.MoveTarget", "ObjectProperty", 4);
        uint actorType = reader.U32(property + 0x84);

        reader.Identity(actorType);

        if (addressOfTarget - controller != 0x200 || reader.U32(property + 0x48) != 0 || reader.U32(property + 0x4C) != 0 ||
            reader.Path(actorType) != "Engine.Actor")
            throw new InvalidDataException("MoveTarget is not the audited scalar Actor property at offset 0x200.");

        uint findPath = VerifyFunction(reader, controllerIdentity.Class, "FindPathToward", 0x24401, 517, 41, 0x951220,
            [("anActor", "ObjectProperty", 0x80u), ("bWeightDetours", "BoolProperty", 0x90u),
             ("MaxPathLength", "IntProperty", 0x90u), ("bReturnPartial", "BoolProperty", 0x90u),
             ("ReturnValue", "ObjectProperty", 0x580u)]);
        uint moveToward = VerifyFunction(reader, controllerIdentity.Class, "MoveToward", 0x24409, 502, 103, 0x8EB890,
            [("NewTarget", "ObjectProperty", 0x80u), ("ViewFocus", "ObjectProperty", 0x90u),
             ("DestinationOffset", "FloatProperty", 0x90u), ("bUseStrafing", "BoolProperty", 0x90u),
             ("bShouldWalk", "BoolProperty", 0x90u)]);
        uint actorReachable = VerifyFunction(reader, controllerIdentity.Class, "ActorReachable", 0x20401, 520, 11, 0x951620,
            [("anActor", "ObjectProperty", 0x80u), ("ReturnValue", "BoolProperty", 0x580u)]);
        uint moveTo = VerifyFunction(reader, controllerIdentity.Class, "MoveTo", 0x24409, 500, 93, 0x8EB4D0,
            [("NewDestination", "StructProperty", 0x80u), ("ViewFocus", "ObjectProperty", 0x90u),
             ("DestinationOffset", "FloatProperty", 0x90u), ("bShouldWalk", "BoolProperty", 0x90u)]);
        uint pointReachable = VerifyFunction(reader, controllerIdentity.Class, "PointReachable", 0x20401, 521, 11, 0x9515B0,
            [("aPoint", "StructProperty", 0x80u), ("ReturnValue", "BoolProperty", 0x580u)]);
        uint vtable = reader.U32(controller);
        uint pawnField = reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);
        uint pawn = reader.U32(pawnField);

        // ActorReachable reads Pawn at +0x1D8 and calls +0x47C. Same-layer runs and complete-body evidence are recorded in report 05.
        if (pawnField - controller != 0x1D8 || pawn < 0x10000 || !reader.IsA(pawn, "TGBioGame.TGBioPawn") ||
            reader.U32(reader.U32(pawn) + 0x47C) != 0x00B77D50 ||
            reader.U32(reader.U32(pawn) + 0x478) != 0x00B64020)
            throw new InvalidDataException("ActorReachable 的当前 Pawn 字段或可达虚槽不同。");

        foreach (var slot in new[] { (0x128u, 0xC102D0u), (0x1B8u, 0x1353C00u), (0x40Cu, 0x914070u),
            (0x470u, 0x8F0AB0u), (0x484u, 0x914140u), (0x480u, 0x8F04F0u), (0x46Cu, 0x8EB740u) })
        {
            if (reader.U32(vtable + slot.Item1) != slot.Item2)
                throw new InvalidDataException("FindPath controller state/movement virtual method differs.");
        }

        byte[] program = BuildBytes(property, point.HasValue ? controller : goal, mode, point);
        uint[] references = point.HasValue ? [property, actorType, moveTo, pointReachable]
            : [goal, property, actorType, findPath, moveToward, actorReachable];
        var result = new AfdevNavigationProgram(program, process, evidence, controllerIdentity, goalIdentity,
            references, mode, point);

        result.ValidateBinding(process, evidence, controller);
        return result;
    }

    private static byte[] BuildBytes(uint property, uint goal, AfdevBioBotNavigationMode mode, AfdevBioBotPoint? point = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        void Reference(byte opcode, uint address)
        {
            writer.Write(opcode);
            writer.Write((ulong)address);
        }

        void Vector(AfdevBioBotPoint value)
        {
            writer.Write((byte)0x23);
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        void FindPath()
        {
            writer.Write(new byte[] { 0x62, 0x05 });
            Reference(0x20, goal);
            writer.Write(new byte[] { 0x28, 0x1D, 0, 0, 0, 0, 0x28, 0x16 });
        }

        if (mode == AfdevBioBotNavigationMode.Point)
        {
            // The same action slot queries and retires PointReachable first; this submits only the destination to native MoveTo.
            writer.Write(new byte[] { 0x61, 0xF4 });
            Vector(point!.Value);
            writer.Write(new byte[] { 0x2A, 0x1E, 0, 0, 0, 0, 0x28, 0x16, 0x08 });
            return stream.ToArray();
        }

        writer.Write((byte)0x0F); // EX_Let assigns the validated MoveTarget.
        Reference(0x01, property);

        if (mode == AfdevBioBotNavigationMode.FindPath)
        {
            FindPath();
        }
        else if (mode == AfdevBioBotNavigationMode.Pursuit)
        {
            // Following the PVE reference, each segment retains the final Pawn: follow directly when reachable, otherwise let the engine choose its next waypoint.
            // Both branches query within the same game Tick, avoiding stale reachability reuse across commands.
            writer.Write(new byte[] { 0x45, 0x62, 0x08 });
            Reference(0x20, goal);
            writer.Write((byte)0x16);
            writer.Write((ushort)9);
            Reference(0x20, goal);
            writer.Write((ushort)19);
            FindPath();
        }
        else
        {
            // EX_Conditional uses lengths relative to current Code, not the original state's Node script base.
            writer.Write(new byte[] { 0x45, 0x62, point.HasValue ? (byte)0x09 : (byte)0x08 });

            if (point is { } destination)
                Vector(destination);
            else
                Reference(0x20, goal);

            writer.Write((byte)0x16);

            byte[] actor = new byte[9];

            actor[0] = 0x20;
            BinaryPrimitives.WriteUInt64LittleEndian(actor.AsSpan(1), goal);
            writer.Write(checked((ushort)actor.Length));
            writer.Write(actor);
            writer.Write((ushort)1);
            writer.Write((byte)0x2A);
        }

        // Coordinate queries use the bound Controller as a boolean-return marker; never interpret a coordinate ID as an object.
        // It enables no physics; later MoveTo clears MoveTarget natively.
        if (mode is not (AfdevBioBotNavigationMode.DirectReachability or AfdevBioBotNavigationMode.PointReachability))
        {
            writer.Write(new byte[] { 0x61, 0xF6 });
            Reference(0x01, property);
            writer.Write(new byte[] { 0x2A, 0x1E, 0, 0, 0, 0, 0x28, 0x28, 0x16 });
        }

        writer.Write((byte)0x08);
        return stream.ToArray();
    }

    public void ValidateBinding(AfdevOwnedProcess process, AfdevNativeEvidence evidence, uint controller)
    {
        var reader = new QueryInspector(process, cacheMetadata: true);

        if (!process.IsAlive || process.ProcessId != processId || process.CreationTimeUtc != processCreationTime ||
            evidence.ProcessId != processId || evidence.ProcessCreationTimeUtc != processCreationTime ||
            !AfdevBotNativeImage.IsVerifiedFor(process, evidence.AfdevSha256) || !AfdevMapCatalog.MatchesBio(evidence) || evidence.World != world ||
            evidence.WorldInfo != worldInfo || controller != controllerIdentity.Address ||
            reader.U32(0x02066BF8) != world || reader.Identity(controller) != controllerIdentity ||
            reader.U32(reader.FieldAddress(controller, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4)) != worldInfo ||
            goalIdentity != null && (reader.Identity(Goal) != goalIdentity ||
                reader.U32(reader.FieldAddress(Goal, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4)) != worldInfo ||
                (reader.U32(Goal + 0x0C) & 0x20000000) != 0 || (reader.U32(Goal + 0xB0) & 8) != 0) ||
            (reader.U32(controller + 0x0C) & 0x20000000) != 0 || (reader.U32(controller + 0xB0) & 8) != 0 ||
            (reader.U32(0x01FCE998) & 1) != 0)
            throw new InvalidDataException("FindPath program lost its exact process/controller/goal/world binding or interpreter assignment mode.");
    }

    private static uint VerifyFunction(QueryInspector reader, uint controllerClass, string name, uint flags,
        ushort native, uint scriptCount, uint exec, (string Name, string Kind, uint Flags)[] parameters)
    {
        string path = "Engine.Controller." + name;
        uint function = reader.FindField(controllerClass, name, path);
        QueryObject identity = reader.Identity(function);
        uint propertiesSize = reader.U32(function + 0x50);
        uint parmsSize = reader.U16(function + 0xBE);
        uint returnOffset = reader.U16(function + 0xC0);
        uint scriptCapacity = reader.U32(function + 0x5C);

        if (reader.Path(identity.Class) != "Core.Function" || reader.U32(function + 0xAC) != flags ||
            reader.U16(function + 0xB0) != native || reader.U32(function + 0xC8) != exec ||
            reader.U32(function + 0x58) != scriptCount || reader.U32(function + 0x54) < 0x10000 ||
            scriptCapacity < scriptCount || scriptCapacity > scriptCount + 4096 ||
            propertiesSize is < 4 or > 64 || parmsSize != propertiesSize)
            throw new InvalidDataException("FindPath indexed native function metadata differs: " + path);

        uint child = reader.U32(function + 0x4C);
        bool hasReturn = false;

        foreach (var parameter in parameters)
        {
            QueryObject field = reader.Identity(child);
            uint offset = reader.U32(child + 0x60);
            uint size = parameter.Kind == "StructProperty" ? 12u : 4u;

            if (field.Outer != function || reader.Path(child) != path + "." + parameter.Name ||
                reader.Path(field.Class) != "Core." + parameter.Kind || reader.U32(child + 0x40) != 1 ||
                reader.U32(child + 0x44) != size || reader.U32(child + 0x48) != parameter.Flags ||
                reader.U32(child + 0x4C) != 0 || size > propertiesSize || offset > propertiesSize - size || (offset & 3) != 0)
                throw new InvalidDataException("FindPath native parameter differs: " + path + "." + parameter.Name);

            if (parameter.Kind == "ObjectProperty" && reader.Path(reader.U32(child + 0x84)) != "Engine.Actor")
                throw new InvalidDataException("FindPath native Actor parameter type differs.");
            if (parameter.Kind == "StructProperty" && (reader.Path(reader.U32(child + 0x84)) != "Core.Object.Vector" ||
                reader.U32(reader.U32(child + 0x84) + 0x50) != 12))
                throw new InvalidDataException("原生坐标参数不是十二字节 Vector。");

            if (parameter.Kind == "BoolProperty")
            {
                uint mask = reader.U32(child + 0x84);

                if (mask == 0 || (mask & (mask - 1)) != 0)
                    throw new InvalidDataException("FindPath native boolean parameter mask differs.");
            }

            if (parameter.Name == "ReturnValue")
            {
                hasReturn = true;

                if (returnOffset != offset)
                    throw new InvalidDataException("FindPath native return offset differs.");
            }

            child = reader.U32(child + 0x3C);
        }

        if (child != 0 || (!hasReturn && returnOffset != 0xFFFF))
            throw new InvalidDataException("FindPath native child chain/return shape differs.");

        return function;
    }
}

/// <summary>
/// Reachability queries neither enable physics nor establish arrival. Movement separately executes native
/// MoveToward or MoveTo.
/// </summary>
public enum AfdevBioBotNavigationMode
{
    FindPath,
    Direct,
    DirectReachability,
    Point,
    PointReachability,
    /// <summary>
    /// Each segment directly reaches or pathfinds toward the same enemy Pawn. Retire if the target becomes
    /// invalid, never falling back to an old Pawn.
    /// </summary>
    Pursuit
}
