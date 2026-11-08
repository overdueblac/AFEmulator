using System.Buffers.Binary;
using AFServer.AssaultFire.Matches.Bots;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Restores revival choice in shared PVE team-wipe detection before admission, modifying only validated
/// script storage in the current process.
/// </summary>
internal sealed class AfdevPveWipeRevivePatch
{
    private const string FunctionPath = "PVEGame.PVEGame.CheckAllPlayerDied";
    private const int ScriptLength = 243;
    private readonly AfdevOwnedProcess process;
    private readonly QueryInspector reader;
    private readonly AfdevNativeEvidence world;
    private readonly uint function;
    private readonly uint script;
    private readonly byte[] original;
    private readonly byte[] replacement;

    // Object references become eight-byte pointers on load. Validate each complete path, zero references for comparison, then check all remaining instructions/jumps.
    private static readonly (int Offset, string Path)[] References =
    [
        (3, FunctionPath + ".bAllPlayerDied"), (15, "Engine.Actor.WorldInfo"), (25, ""),
        (35, "Engine.WorldInfo.AllControllers"), (44, "PVEGame.PVEPlayerController"),
        (53, FunctionPath + ".PC"), (66, FunctionPath + ".PRI"), (75, "PVEGame.PVEPlayerReplicationInfo"),
        (85, FunctionPath + ".PC"), (95, "Engine.Controller.PlayerReplicationInfo"),
        (105, "Engine.Controller.PlayerReplicationInfo"), (120, FunctionPath + ".PRI"),
        (136, FunctionPath + ".PRI"), (146, "UTGame.TGPlayerReplicationInfo.bAlwaysObserver"),
        (157, "UTGame.TGPlayerReplicationInfo.bAlwaysObserver"), (173, FunctionPath + ".PRI"),
        (183, "UTGame.TGPlayerReplicationInfo.bDead"), (194, "UTGame.TGPlayerReplicationInfo.bDead"),
        (207, FunctionPath + ".bAllPlayerDied"), (224, FunctionPath + ".bAllPlayerDied"),
        (234, FunctionPath + ".ReturnValue")
    ];

    internal AfdevPveWipeRevivePatch(AfdevOwnedProcess process, AfdevNativeEvidence world)
    {
        this.process = process;
        this.world = world;
        reader = new QueryInspector(process);
        function = reader.FindField(world.GameInfoClass, "CheckAllPlayerDied", FunctionPath);
        script = reader.U32(function + 0x54);

        if (reader.Path(reader.Identity(function).Class) != "Core.Function" ||
            reader.U16(function + 0xB0) != 0 || reader.U32(function + 0xC8) != 0x0048DB10 ||
            reader.U32(function + 0x58) != ScriptLength || reader.U32(function + 0x5C) < ScriptLength)
            throw new InvalidDataException("PVE 灭团检测函数布局与已核验版本不同。");

        original = process.ReadMemory(script, ScriptLength);

        byte[] normalized = original.ToArray();

        foreach (var (offset, path) in References)
        {
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset));

            if (BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset + 4)) != 0 ||
                (path.Length == 0 ? pointer != 0 : reader.Path(reader.Identity(pointer).Address) != path))
                throw new InvalidDataException("PVE 灭团检测脚本引用不同：" + path);

            normalized.AsSpan(offset, 8).Clear();
        }

        byte[] expected = Convert.FromHexString(
            "142D000000000000000000272F19010000000000000000BB000000000000000000001C000000000000000020000000000000000000000000000000000016DC000F0000000000000000002E000000000000000019000000000000000000090000000000000000000001000000000000000007DB008282770000000000000000002A1618220081190000000000000000000A000000000000000000002D010000000000000000161618220081190000000000000000000A000000000000000000002D0100000000000000001616142D0000000000000000002806DC003130042D000000000000000000043A000000000000000053");

        if (!normalized.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("PVE 灭团检测原始指令或跳转与已核验版本不同。");

        uint controllerClass = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(44));
        uint canRevive = reader.FindField(controllerClass, "CanRevive", "PVEGame.PVEPlayerController.CanRevive");
        uint returnValue = reader.FindField(canRevive, "ReturnValue", "PVEGame.PVEPlayerController.CanRevive.ReturnValue");
        var code = new List<byte>(ScriptLength);

        // Retain nonnull PRI and observer exclusions. While dead players may still revive, do not publish an all-players-dead event to the map.
        // Return false directly from the iterator, avoiding temporary boolean assignment/unreachable return while preserving original allocation and length.
        code.AddRange(original.AsSpan(12, 158).ToArray());
        code.Add(0x84); // Boolean || with original !PRI.bDead as the left operand.
        code.AddRange(original.AsSpan(170, 33).ToArray());
        code.AddRange(new byte[] { 0x18, 32, 0, 0x19, 0 });
        code.AddRange(original.AsSpan(53, 8).ToArray());
        code.AddRange(new byte[] { 10, 0 });
        code.AddRange(BitConverter.GetBytes(returnValue));
        code.AddRange(new byte[] { 0, 0, 0, 0, 0, 0x1B });
        code.AddRange(process.ReadMemory(canRevive + 0x2C, 8));
        code.AddRange(new byte[] { 0x16, 0x16, 0x16, 0x30, 0x04, 0x28, 0x31, 0x30, 0x04, 0x27 });
        code.AddRange(Enumerable.Repeat((byte)0x0B, 7)); // Pad the unreachable tail with Nothing without changing UStruct memory ownership.
        code.Add(0x53);
        replacement = code.ToArray();

        if (replacement.Length != ScriptLength)
            throw new InvalidDataException("PVE 灭团检测修复超出原脚本长度。");

        BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(11), 211); // WorldInfo context -> after iterator completion.
        BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(50), 232); // IteratorPop for AllControllers.
        BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(102), 231); // IteratorNext when the condition fails.
        BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(156), 70); // Expand the right operand of && to !Dead || CanRevive.
    }

    /// <summary>
    /// The caller holds the host operation gate and suspends the main thread. This method does not acquire
    /// thread ownership itself.
    /// </summary>
    internal void Install()
    {
        if (reader.U32(AfdevNativeBootstrap.GWorld) != world.World ||
            reader.U32(world.NetDriver + 0x48) != 0 || reader.U32(function + 0x54) != script ||
            reader.U32(function + 0x58) != ScriptLength ||
            !process.ReadMemory(script, ScriptLength).AsSpan().SequenceEqual(original))
            throw new InvalidDataException("PVE 灭团检测安装前世界、连接或脚本发生变化。");

        process.WriteMemory(script, replacement);

        if (!process.ReadMemory(script, ScriptLength).AsSpan().SequenceEqual(replacement))
            throw new InvalidDataException("PVE 灭团检测修复回读不一致。");
    }
}
