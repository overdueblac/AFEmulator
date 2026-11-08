using System.Security.Cryptography;
using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Read-only descriptions of existing engine physics and latent-cancellation functions. Callers still
/// validate internal identity, world, Pawn generation, and frozen gates on the game thread. Publishes no
/// commands; an entry point's existence is not movement acceptance.
/// </summary>
internal sealed class AfdevBioBotMotionFunctions(AfdevOwnedProcess process)
{
    public const uint AutonomousPhysicsExec = 0x021EAD7B;
    public const uint AutonomousPhysicsBody = 0x00B95F00;
    public const int AutonomousPhysicsBodyLength = 160;
    public const string AutonomousPhysicsBodyHash = "f9702fdd6bcc83fd154805ef60df571472348c2a7c9373e5f6fe2f8c1f3df106";
    public const uint AutonomousPhysicsDispatchReturn = 0x00B95F9D;
    public const uint StopLatentExecutionExec = 0x00951790;
    private readonly QueryInspector reader = new(process, cacheMetadata: true);

    public ResourceFunction DescribeAutonomousPhysics(uint pawn) => Describe(pawn, "Engine.Pawn",
        "Engine.Actor.AutonomousPhysics", AutonomousPhysicsExec, 3971, 4, 11,
        "36e9d7d0f68b9f7d4f9325a595aff51bc1d5c3d05e565e1ac4bcdda526180ea9");

    public ResourceFunction DescribeStopLatentExecution(uint controller) => Describe(controller,
        "TGBioGame.TGBioPlayerController", "Engine.Controller.StopLatentExecution", StopLatentExecutionExec,
        0, 0, 2, "ccaf083429bf5a89fa542d244998f2bbd2f8837281164524d82f612695ccb1db");

    private ResourceFunction Describe(uint receiver, string requiredAncestor, string path, uint expectedExec,
        ushort nativeIndex, ushort parameterSize, uint scriptCount, string packageHash)
    {
        AfdevMotionNativeImage.Verify(process);

        // Reflection still validates native3971 and its protected-entry provenance, but ProcessEvent rejects this index.
        // Actual physics uses the fully verified original ECX/float/ret4 ABI. The body calls +0x22C, which the observer counts.
        if (parameterSize != 0 && TickHook.Hash(process.ReadMemory(AutonomousPhysicsBody, AutonomousPhysicsBodyLength))
                != AutonomousPhysicsBodyHash)
            throw new InvalidDataException("AutonomousPhysics 完整主体或栈清理 ABI 发生变化。");

        QueryObject identity = reader.Identity(receiver);
        uint ancestor = identity.Class;
        HashSet<uint> seen = [];

        while (reader.Path(ancestor) != requiredAncestor)
        {
            if (!seen.Add(ancestor) || seen.Count > 32)
                throw new InvalidDataException("原生物理接收对象不具备所需引擎类型。");

            ancestor = reader.U32(ancestor + 0x48);
            reader.Identity(ancestor);
        }

        uint function = reader.FindField(identity.Class, path[(path.LastIndexOf('.') + 1)..], path);
        QueryObject functionIdentity = reader.Identity(function);
        uint script = reader.U32(function + 0x54);
        uint capacity = reader.U32(function + 0x5C);
        uint child = reader.U32(function + 0x4C);
        List<ResourceParameter> parameters = [];

        if (reader.Path(functionIdentity.Class) != "Core.Function" || reader.U32(function + 0xAC) != 0x20401
            || reader.U16(function + 0xB0) != nativeIndex || reader.U32(function + 0x50) != parameterSize
            || reader.U16(function + 0xBE) != parameterSize || reader.U16(function + 0xC0) != 0xFFFF
            || reader.U32(function + 0xC8) != expectedExec || reader.U32(function + 0x58) != scriptCount
            || capacity < scriptCount || capacity > scriptCount + 4096 || script < 0x10000
            || reader.U32(reader.U32(receiver) + 0x120) != 0x0075A9F0)
            throw new InvalidDataException("原生物理或取消函数元数据不符：" + path);

        if (parameterSize != 0)
        {
            QueryObject property = reader.Identity(child);

            if (property.Outer != function || reader.Path(child) != path + ".DeltaSeconds"
                || reader.Path(property.Class) != "Core.FloatProperty" || reader.U32(child + 0x40) != 1
                || reader.U32(child + 0x44) != 4 || reader.U32(child + 0x60) != 0
                || reader.U32(child + 0x48) != 0x80 || reader.U32(child + 0x4C) != 0)
                throw new InvalidDataException("AutonomousPhysics 的浮点参数布局不符。");

            parameters.Add(new ResourceParameter(property, "DeltaSeconds", "FloatProperty", 0, 4, 0x80, 0, null));
            child = reader.U32(child + 0x3C);
        }

        if (child != 0)
            throw new InvalidDataException("原生物理或取消函数含额外参数。");

        byte[] bytes = process.ReadMemory(script, checked((int)scriptCount));
        byte[] expectedScript = parameterSize == 0 ? [0x0B, 0x53] : [0x29, 0, 0, 0, 0, 0, 0, 0, 0, 0x0B, 0x53];

        // The stored NativeParm reference is export 102, relocated at runtime to an eight-byte UObject reference.
        if (parameterSize != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(expectedScript.AsSpan(1, 4), parameters[0].Identity.Address);
        if (!bytes.AsSpan().SequenceEqual(expectedScript))
            throw new InvalidDataException("原生物理或取消脚本的精确重定位字节不符。");

        return new ResourceFunction(identity, functionIdentity, path, 0x20401, nativeIndex, parameterSize,
            parameterSize, 0xFFFF, expectedExec, script, scriptCount, capacity,
            Convert.ToHexString(SHA256.HashData(bytes)), packageHash, parameters);
    }
}
