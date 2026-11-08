using System.Buffers.Binary;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
sealed partial class FactoryInspector(AfdevOwnedProcess process)
{
    public QueryInspector Reader { get; } = new(process);

    public FactoryFunction Resolve(AfdevNativeEvidence evidence)
    {
        var r = Reader;

        if (r.Path(evidence.GameInfoClass) != "TGBioGame.TGBioMatch" || r.U32(evidence.GameInfo + 0x34) != evidence.GameInfoClass)
            throw new InvalidDataException("Exact ordinary BIO receiver differs.");

        uint function = r.FindField(evidence.GameInfoClass, "SpawnPlayerController", "Engine.GameInfo.SpawnPlayerController");

        if (r.Path(r.U32(function + 0x34)) != "Core.Function" || r.U32(function + 0xAC) != 0x20002 || r.U16(function + 0xB0) != 0 || r.U32(function + 0x50) != 28 || r.U16(function + 0xBE) != 28 || r.U16(function + 0xC0) != 24 || r.U32(function + 0x58) != 53 || r.U32(function + 0x5C) != 53 || r.U32(r.U32(evidence.GameInfo) + 0x120) != FactoryHook.ActorProcessEvent)
            throw new InvalidDataException("SpawnPlayerController exact function layout differs.");

        var properties = new List<FactoryProperty>();
        uint child = r.U32(function + 0x4C);

        foreach (var expected in new[]
        {
            ("SpawnLocation", "Core.StructProperty", 12u, 0x80u, 0u),
            ("SpawnRotation", "Core.StructProperty", 12u, 0x80u, 12u),
            ("ReturnValue", "Core.ObjectProperty", 4u, 0x580u, 24u)
        }

        )
        {
            if (child == 0)
                throw new InvalidDataException("Factory parameter child missing.");
            r.Identity(child);

            var item = new FactoryProperty(child, r.U32(child + 0x3C), r.U32(child + 0x40), r.U32(child + 0x44), r.U32(child + 0x48), r.U32(child + 0x4C), r.U32(child + 0x60), r.U32(child + 0x84));

            if (r.Path(child) != "Engine.GameInfo.SpawnPlayerController." + expected.Item1 || r.U32(child + 0x28) != function || r.Path(r.U32(child + 0x34)) != expected.Item2 || item.Dimension != 1 || item.Size != expected.Item3 || item.Flags != expected.Item4 || item.HighFlags != 0 || item.Offset != expected.Item5)
                throw new InvalidDataException("Factory parameter differs: " + expected.Item1);

            string expectedType = expected.Item1 == "SpawnLocation" ? "Core.Object.Vector" : expected.Item1 == "SpawnRotation" ? "Core.Object.Rotator" : "Engine.PlayerController";

            if (r.Path(item.Type) != expectedType)
                throw new InvalidDataException("Factory parameter type differs: " + expectedType);
            properties.Add(item);
            child = item.Next;
        }

        if (child != 0)
            throw new InvalidDataException("Unexpected fourth factory parameter.");

        uint pcClassAddress = r.FieldAddress(evidence.GameInfo, "PlayerControllerClass", "Engine.GameInfo.PlayerControllerClass", "ClassProperty", 4);
        uint priClassAddress = r.FieldAddress(evidence.GameInfo, "PlayerReplicationInfoClass", "Engine.GameInfo.PlayerReplicationInfoClass", "ClassProperty", 4);
        uint pcClass = r.U32(pcClassAddress), priClass = r.U32(priClassAddress);

        if (r.Path(pcClass) != "TGBioGame.TGBioPlayerController" || r.Path(priClass) != "TGBioGame.TGBioPlayerReplicationInfo")
            throw new InvalidDataException("Ordinary BIO factory classes differ.");
        return new FactoryFunction(function, r.U32(function + 0x34), r.U32(function + 0x28), r.U32(function + 0xC8), r.U32(function + 0x54), r.U32(function + 0x58), r.U32(function + 0x5C), properties, pcClassAddress, pcClass, priClassAddress, priClass);
    }

    public FactoryLocation FindControllerLocation(AfdevNativeEvidence evidence)
    {
        var r = Reader;
        uint level = r.U32(evidence.World + 0x50), data = r.U32(level + 0x3C), count = r.U32(level + 0x40);

        if (data == 0 || count == 0 || count > 200000 || r.U32(level + 0x44) < count)
            throw new InvalidDataException("Actor array bounds differ.");

        byte[] actors = process.ReadMemory(data, checked((int)count * 4));

        for (int i = 0; i < count; i++)
        {
            uint actor = BinaryPrimitives.ReadUInt32LittleEndian(actors.AsSpan(i * 4, 4));

            if (actor == 0 || !r.IsA(actor, "Engine.PlayerStart"))
                continue;
            r.Identity(actor);

            uint worldInfo = r.U32(r.FieldAddress(actor, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4));

            if (worldInfo != evidence.WorldInfo || (r.U32(actor + 0xB0) & 8) != 0)
                continue;

            uint location = r.FieldAddress(actor, "Location", "Engine.Actor.Location", "StructProperty", 12);
            uint rotation = r.FieldAddress(actor, "Rotation", "Engine.Actor.Rotation", "StructProperty", 12);
            byte[] locationBytes = process.ReadMemory(location, 12), rotationBytes = process.ReadMemory(rotation, 12);

            if (Enumerable.Range(0, 3).Any(index => !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(locationBytes.AsSpan(index * 4, 4)))))
                throw new InvalidDataException("Controller location contains nonfinite values.");
            return new FactoryLocation(actor, r.Path(r.U32(actor + 0x34)), locationBytes, rotationBytes);
        }

        throw new InvalidDataException("No live PlayerStart to locate a test Controller.");
    }

    public object Snapshot(AfdevNativeEvidence evidence, uint controller = 0)
    {
        var r = Reader;

        uint ReadField(uint obj, string name, string path, string kind = "ObjectProperty") => r.U32(r.FieldAddress(obj, name, path, kind, 4));

        var counts = new Dictionary<string, uint>();

        foreach (string name in new[]
        {
            "NumPlayers",
            "NumTravellingPlayers",
            "NumBots",
            "NumSpectators"
        }

        )
            counts.Add(name, ReadField(evidence.GameInfo, name, "Engine.GameInfo." + name, "IntProperty"));

        uint level = r.U32(evidence.World + 0x50), data = r.U32(level + 0x3C), count = r.U32(level + 0x40);

        if (count > 200000 || data == 0)
            throw new InvalidDataException("Post-factory actors differ.");

        byte[] rawActors = process.ReadMemory(data, (int)count * 4);
        uint[] actors = Enumerable.Range(0, (int)count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(rawActors.AsSpan(i * 4, 4))).ToArray();
        uint pri = controller == 0 ? 0 : ReadField(controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo");
        uint player = controller == 0 ? 0 : ReadField(controller, "Player", "Engine.PlayerController.Player");
        uint pawn = controller == 0 ? 0 : ReadField(controller, "Pawn", "Engine.Controller.Pawn");
        uint stateFrame = controller == 0 ? 0 : r.U32(controller + 0x18);
        object? state = stateFrame == 0 ? null : new SnapshotJson1
        {
            Address = stateFrame,
            Node = r.U32(stateFrame + 0x10),
            Object = r.U32(stateFrame + 0x14),
            Code = r.U32(stateFrame + 0x18),
            StateNode = r.U32(stateFrame + 0x28),
            LatentAction = r.U16(stateFrame + 0x30)
        };
        object? controllerInfo = controller == 0 ? null : new SnapshotJson2
        {
            Identity = r.Identity(controller),
            Class = r.Path(r.U32(controller + 0x34)),
            WorldInfo = ReadField(controller, "WorldInfo", "Engine.Actor.WorldInfo"),
            player = player,
            pawn = pawn,
            pri = pri,
            NavigationHandle = ReadField(controller, "NavigationHandle", "Engine.Controller.NavigationHandle"),
            StateFrame = state,
            InActors = actors.Contains(controller),
            ActorFlags = r.U32(controller + 0xB0)
        };
        object? priInfo = pri == 0 ? null : new SnapshotJson3
        {
            Identity = r.Identity(pri),
            Class = r.Path(r.U32(pri + 0x34)),
            Owner = ReadField(pri, "Owner", "Engine.Actor.Owner"),
            WorldInfo = ReadField(pri, "WorldInfo", "Engine.Actor.WorldInfo"),
            Team = ReadField(pri, "Team", "Engine.PlayerReplicationInfo.Team"),
            InActors = actors.Contains(pri)
        };

        if (controller != 0 && (r.Path(r.U32(controller + 0x34)) != "TGBioGame.TGBioPlayerController" || player != 0 || pawn != 0 || pri == 0 || r.Path(r.U32(pri + 0x34)) != "TGBioGame.TGBioPlayerReplicationInfo" || !actors.Contains(controller) || !actors.Contains(pri) || ReadField(pri, "Owner", "Engine.Actor.Owner") != controller))
            throw new InvalidDataException("Created Controller/PRI is not the exact unconnected ordinary BIO factory pair.");
        return new SnapshotJson4
        {
            World = r.U32(0x02066BF8),
            ExpectedWorld = evidence.World,
            GameInfo = evidence.GameInfo,
            counts = counts,
            Level = level,
            ActorData = data,
            ActorCount = count,
            Controller = controllerInfo,
            Pri = priInfo
        };
    }

    public object NativeSlots() => new NativeSlotsJson1
    {
        Table = 0x01FF4D10u,
        Slots = new[]
        {
            0x08,
            0x1E,
            0x20,
            0x28,
            0x2A,
            0x61,
            0x62,
            256,
            384,
            500,
            501,
            502,
            503,
            517
        }.ToDictionary(index => index, index => Reader.U32(0x01FF4D10u + (uint)index * 4))
    };
}

sealed record FactoryProperty(uint Address, uint Next, uint Dimension, uint Size, uint Flags, uint HighFlags, uint Offset, uint Type);
sealed record FactoryFunction(uint Function, uint FunctionClass, uint FunctionOwner, uint ExecPointer, uint ScriptPointer, uint ScriptCount, uint ScriptCapacity, IReadOnlyList<FactoryProperty> Properties, uint ControllerClassAddress, uint ControllerClass, uint PriClassAddress, uint PriClass);
sealed record FactoryLocation(uint Actor, string Class, byte[] Location, byte[] Rotation);
