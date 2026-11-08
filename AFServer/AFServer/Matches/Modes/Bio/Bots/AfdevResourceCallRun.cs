using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
static partial class ResourceCallRun
{
    private static readonly ResourceMethodSpec[] Methods = [new(1, "ChangeUin", 0x20002, 12, 12, 0xFFFF, 22, "TargetUin", "Core.StrProperty", 12, 0x400080), new(2, "SetDefaultAvatarInfo", 0x20102, 0, 0, 0xFFFF, 1253, null, null, 0, 0), new(3, "RecoverSyncArrayInfo", 0x20401, 4, 4, 0, 2, "ReturnValue", "Core.BoolProperty", 4, 0x580)];
    public static async Task<ResourceInitialization> ExecuteAsync(AfdevOwnedProcess process, AfdevNativeEvidence evidence, uint controller, uint commandAddress, Action<string, object> save, Action<string> report, CancellationToken cancellationToken, string botUin, uint preparationCommand = 0)
    {
        if (botUin.Length != 16 || !botUin.StartsWith("bot-", StringComparison.Ordinal) || botUin.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("Bot Uin 必须使用独立 bot-xxxxxxxxxxxx 身份域。", nameof(botUin));

        byte[] inputBytes = System.Text.Encoding.Unicode.GetBytes(botUin + "\0");
        uint inputCount = checked((uint)botUin.Length + 1);
        var inspector = new BioResourceInspector(process);
        var reader = inspector.Reader;
        ResourceProfile profile = inspector.Prepare(evidence, controller);
        uint pri = profile.Pri.Address;

        if (preparationCommand != 0)
            AfdevBioBotPreparationInspector.RequirePreparing(process, evidence, preparationCommand);

        if (profile.Uin.Value.Length != 0 || profile.SyncList.Count != 0 || profile.HudCamp != 0 || profile.Team != 0 ||
            reader.U32(commandAddress + ResourceCallHook.OrdinalOffset) != 0 ||
            (preparationCommand == 0 && reader.U32(reader.FieldAddress(evidence.GameInfo, "NumPlayers",
                "Engine.GameInfo.NumPlayers", "IntProperty", 4)) != 0))
            throw new InvalidDataException("资源初始化要求本局未触碰的 PRI 与独立序号，真人并行加载必须持有准备门。");
        save("resource-before", profile);

        var completed = new List<string>();
        bool recovered = false;

        foreach (ResourceMethodSpec spec in Methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            profile = inspector.Capture();
            if (spec.Ordinal == 2 && (profile.SyncList.Count != 0 || profile.HudCamp != 0 || profile.Team != 0 || profile.Uin.Value != botUin))
                throw new InvalidDataException("SetDefaultAvatarInfo is additive; it must never run twice on a nonempty SyncList.");

            ResourceMethod method = Resolve(reader, pri, spec, save);
            uint uinProperty = reader.FindField(profile.Pri.Class, "Uin", "UTGame.TGPlayerReplicationInfo.Uin");
            uint syncProperty = reader.FindField(profile.Pri.Class, "SyncList", "UTGame.TGPlayerReplicationInfo.SyncList");

            if (spec.Ordinal == 1)
                VerifyChangeUinScript(process, method, uinProperty);

            uint[] roots = method.Parameter == null ? [evidence.GameInfo, controller, pri, method.Identity.Address, uinProperty, syncProperty] : [evidence.GameInfo, controller, pri, method.Identity.Address, uinProperty, syncProperty, method.Parameter.Identity.Address];
            IReadOnlyList<QueryObject> identities = reader.IdentityClosure(roots);
            uint parms = commandAddress + 0xD00, inputString = commandAddress + 0xD80;
            byte[] parameters = new byte[Math.Max((int)spec.ParmsSize, 4)];

            if (spec.Ordinal == 1)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(0, 4), inputString);
                BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4, 4), inputCount);
                BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(8, 4), inputCount);
            }

            if (spec.Ordinal == 3)
                BinaryPrimitives.WriteUInt32LittleEndian(parameters, uint.MaxValue);

            byte[] command = new byte[ResourceCallHook.CommandSize];

            void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(offset, 4), value);
            Set(0x40, process.MainThreadId);
            Set(0x44, reader.U32(0x02063820));
            Set(0x48, evidence.World);
            Set(0x4C, evidence.WorldInfo);
            Set(0x50, evidence.GameInfo);
            Set(0x54, evidence.GameInfoClass);
            Set(0x58, method.Identity.Address);
            Set(0x5C, parms);
            Set(0x60, method.Identity.Class);
            Set(0x64, method.Identity.Outer);
            Set(0x70, controller);
            Set(0x74, pri);
            Set(0x78, profile.Controller.Class);
            Set(0x7C, profile.Pri.Class);
            Set(0x80, method.Exec);
            Set(0x84, method.Script);
            Set(0xA0, method.Identity.NameIndex);
            Set(0xA4, method.Identity.NameNumber);
            Set(0xA8, method.Identity.Index);
            Set(0xAC, spec.Ordinal);
            Set(0xB0, method.Parameter?.Identity.Address ?? 0);
            Set(0xC0, profile.Uin.Address);
            Set(0xC4, (uint)identities.Count);
            Set(0xC8, reader.FieldAddress(controller, "PlayerReplicationInfo", "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));
            Set(0xCC, reader.FieldAddress(pri, "Owner", "Engine.Actor.Owner", "ObjectProperty", 4));
            Set(0xD0, reader.FieldAddress(controller, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4));
            Set(0xD4, reader.FieldAddress(pri, "WorldInfo", "Engine.Actor.WorldInfo", "ObjectProperty", 4));
            Set(0xD8, reader.FieldAddress(controller, "Player", "Engine.PlayerController.Player", "ObjectProperty", 4));
            Set(0xDC, reader.FieldAddress(controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4));
            Set(0xE0, profile.SyncList.Address);
            Set(0xE4, inputString);
            Set(0xE8, profile.SyncList.Count);
            Set(0xEC, reader.FieldAddress(pri, "Team", "Engine.PlayerReplicationInfo.Team", "ObjectProperty", 4));
            Set(0xF4, inputCount);
            Set(0xF0, reader.FieldAddress(pri, "HudCamp", "UTGame.TGPlayerReplicationInfo.HudCamp", "IntProperty", 4));
            Set((int)ResourceCallHook.PreparationPage, preparationCommand);

            int cursor = 0x100;

            foreach (QueryObject identity in identities)
            {
                foreach (uint value in new[]
                {
                    identity.Address,
                    identity.Index,
                    identity.Outer,
                    identity.NameIndex,
                    identity.NameNumber,
                    identity.Class
                }

                )
                {
                    Set(cursor, value);
                    cursor += 4;
                }
            }

            save("resource-prepared-" + spec.Name, new ExecuteAsyncJson1 { method = method, identities = identities, commandAddress = commandAddress, parms = parms, inputString = inputString, ParametersHex = Convert.ToHexString(parameters), CommandHex = Convert.ToHexString(command), ScriptHex = Convert.ToHexString(process.ReadMemory(method.Script, (int)method.ScriptCount)), ScriptSha256 = TickHook.Hash(process.ReadMemory(method.Script, (int)method.ScriptCount)) });
            // Native completion order is stored separately at +F00. Unknown completion never reaches here; never reset started and resend.
            if (spec.Ordinal > 1 && (reader.U32(commandAddress + 8) != 1 || reader.U32(commandAddress + 12) != 1 || reader.U32(commandAddress + ResourceCallHook.OrdinalOffset) != spec.Ordinal - 1))
                throw new InvalidDataException("Previous resource command lacks confirmed stable completion; no retry.");

            CommandIdleAckReceipt idle = await DisarmAndWaitAsync(process, commandAddress, cancellationToken);

            save("resource-idle-ack-" + spec.Name, idle);
            process.WriteMemory(commandAddress + 4, command.AsSpan(4).ToArray());
            process.WriteMemory(parms, parameters);
            if (spec.Ordinal == 1)
            {
                process.WriteMemory(inputString, inputBytes);
                process.WriteMemory(commandAddress + 0xE00, inputBytes);
            }

            if (!process.ReadMemory(commandAddress, command.Length).AsSpan().SequenceEqual(command) || !process.ReadMemory(parms, parameters.Length).AsSpan().SequenceEqual(parameters))
                throw new InvalidDataException("Unarmed resource payload readback differs.");
            if (spec.Ordinal == 1 &&
                (!process.ReadMemory(inputString, inputBytes.Length).AsSpan().SequenceEqual(inputBytes) ||
                 !process.ReadMemory(commandAddress + 0xE00, inputBytes.Length).AsSpan().SequenceEqual(inputBytes)))
                throw new InvalidDataException("Resource borrowed Uin readback differs.");
            report("RESOURCE_ARMED " + spec.Name + " ordinal=" + spec.Ordinal);

            QueryObservation observation = await InvokeAsync(process, evidence, commandAddress, parms, spec, save, cancellationToken);

            completed.Add(spec.Name);
            profile = inspector.Capture();
            save("resource-after-" + spec.Name, new ExecuteAsyncJson2 { profile = profile, observation = observation, completed = completed });
            if (spec.Ordinal == 1)
            {
                bool independent = profile.Uin.Value == botUin && profile.Uin.Data != inputString && profile.Uin.Count == inputCount;

                save("resource-uin-ownership", new ExecuteAsyncJson3 { independent = independent, Uin = profile.Uin, BorrowedInputData = inputString, ParametersAfter = Convert.ToHexString(process.ReadMemory(parms, 12)), InputAfter = Convert.ToHexString(process.ReadMemory(inputString, inputBytes.Length)), RetainedUntilOwnedProcessStops = true });
                if (!independent || !process.ReadMemory(parms, 12).AsSpan().SequenceEqual(parameters) || !process.ReadMemory(inputString, inputBytes.Length).AsSpan().SequenceEqual(inputBytes))
                    throw new InvalidDataException("ChangeUin did not produce an independent engine-owned FString while preserving borrowed input.");
            }

            if (spec.Ordinal == 2 && profile.SyncList.Count == 0)
                throw new InvalidDataException("SetDefaultAvatarInfo returned but did not populate the once-only SyncList.");
            if (spec.Ordinal == 3)
            {
                if (observation.Result is not (0 or 1) || observation.ParmsValue != observation.Result)
                    throw new InvalidDataException("RecoverSyncArrayInfo did not write a canonical bool return.");
                recovered = observation.Result == 1;

                ResourceAvatarResult verified = inspector.VerifyDefaultAvatar(botUin, 0, recovered);

                save("resource-default-verification", verified);
                if (!verified.Complete)
                    throw new InvalidDataException("Native recovery returned, but the exact default avatar/bag evidence is incomplete.");
            }

            report("RESOURCE_COMPLETED " + spec.Name + " ordinal=" + spec.Ordinal);
        }

        return new ResourceInitialization(profile, recovered, completed);
    }

    private static async Task<QueryObservation> InvokeAsync(AfdevOwnedProcess process, AfdevNativeEvidence evidence, uint command, uint parms, ResourceMethodSpec spec, Action<string, object> save, CancellationToken cancellationToken)
    {
        var observations = new List<QueryObservation>();

        try
        {
            process.WriteMemory(command, [1, 0, 0, 0]);

            var timer = Stopwatch.StartNew();

            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.IsAlive)
                    throw new IOException("Resource receiver process exited; unknown completion is not retried.");

                QueryObservation sample = QueryRun.Read(process, command, parms);

                observations.Add(sample);
                if (sample.Complete == 1)
                {
                    if (sample.Requested != 1 || sample.Started != 1 || sample.Status != 1 || sample.LastGuard != 0 || sample.ThreadBefore != process.MainThreadId || sample.ThreadAfter != process.MainThreadId || sample.StackBefore == 0 || sample.StackBefore != sample.StackAfter || sample.WorldBefore != evidence.World || sample.WorldAfter != evidence.World || sample.GameBefore != evidence.GameInfo || sample.GameAfter != evidence.GameInfo || sample.ClassBefore != evidence.GameInfoClass || sample.ClassAfter != evidence.GameInfoClass || BinaryPrimitives.ReadUInt32LittleEndian(process.ReadMemory(command + ResourceCallHook.OrdinalOffset, 4)) != spec.Ordinal)
                        throw new InvalidDataException("Returned resource call lost its exact thread/world/ordinal/stack identity.");
                    return sample;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("Resource call deadline elapsed; started or blocked call is not retried.");
        }
        finally
        {
            save("resource-observations-" + spec.Name, observations);
            if (process.IsAlive)
                save("resource-final-" + spec.Name, new InvokeAsyncJson1 { Observation = QueryRun.Read(process, command, parms), NativeCompletedOrdinal = BinaryPrimitives.ReadUInt32LittleEndian(process.ReadMemory(command + ResourceCallHook.OrdinalOffset, 4)), CommandHex = Convert.ToHexString(process.ReadMemory(command, ResourceCallHook.CommandSize)) });
        }
    }

    // request=0 alone cannot exclude a main thread that already read request=1. Await confirmation that it reached the branch reading no payload.
    public static async Task<CommandIdleAckReceipt> DisarmAndWaitAsync(AfdevOwnedProcess process, uint commandAddress, CancellationToken cancellationToken)
    {
        uint Read(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(process.ReadMemory(commandAddress + offset, 4));

        uint before = Read(ResourceCallHook.IdleAckOffset);

        process.WriteMemory(commandAddress, [0, 0, 0, 0]);

        var timer = Stopwatch.StartNew();

        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.IsAlive)
                throw new IOException("Owned process exited before command IdleAck; payload is not republished.");

            uint after = Read(ResourceCallHook.IdleAckOffset);

            if (after != before)
                return new CommandIdleAckReceipt(before, after, DateTimeOffset.UtcNow);
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException("No native IdleAck after disarming; payload is not overwritten or republished.");
    }

    private static ResourceMethod Resolve(QueryInspector reader, uint pri, ResourceMethodSpec spec, Action<string, object> save)
    {
        uint function = reader.FindField(reader.U32(pri + 0x34), spec.Name, "UTGame.TGPlayerReplicationInfo." + spec.Name);
        uint child = reader.U32(function + 0x4C);
        ResourceCallParameter? parameter = child == 0 ? null : new ResourceCallParameter(reader.Identity(child), reader.U32(child + 0x3C), reader.U32(child + 0x40), reader.U32(child + 0x44), reader.U32(child + 0x48), reader.U32(child + 0x4C), reader.U32(child + 0x60), spec.Ordinal == 3 ? reader.U32(child + 0x84) : 0);
        var method = new ResourceMethod(spec, reader.Identity(function), reader.U32(function + 0xAC), reader.U16(function + 0xB0), reader.U32(function + 0x50), reader.U16(function + 0xBE), reader.U16(function + 0xC0), reader.U32(function + 0xC8), reader.U32(function + 0x54), reader.U32(function + 0x58), reader.U32(function + 0x5C), parameter);

        save("resource-descriptor-" + spec.Name, method);
        if (reader.Path(method.Identity.Class) != "Core.Function" || reader.Path(method.Identity.Outer) != "UTGame.TGPlayerReplicationInfo" || method.Flags != spec.Flags || method.NativeIndex != 0 || method.PropertiesSize != spec.PropertiesSize || method.ParmsSize != spec.ParmsSize || method.ReturnOffset != spec.ReturnOffset || method.ScriptCount != spec.ScriptSize || method.ScriptCapacity != method.ScriptCount || method.Exec != (spec.Ordinal == 3 ? 0x013AFF40u : 0x0048DB10u) || method.Script < 0x10000 || reader.U32(reader.U32(pri) + 0x120) != QueryHook.ActorProcessEvent)
            throw new InvalidDataException("White-listed resource function layout differs: " + spec.Name);
        if (spec.ParameterName == null)
        {
            if (parameter != null)
                throw new InvalidDataException("Unexpected resource parameter child.");
        }
        else if (parameter == null || reader.Path(parameter.Identity.Address) != "UTGame.TGPlayerReplicationInfo." + spec.Name + "." + spec.ParameterName || parameter.Identity.Outer != function || reader.Path(parameter.Identity.Class) != spec.ParameterClass || parameter.Next != 0 || parameter.Dimension != 1 || parameter.Size != spec.ParameterSize || parameter.Flags != spec.ParameterFlags || parameter.HighFlags != 0 || parameter.Offset != 0 || (spec.Ordinal == 3 && parameter.BoolMask != 1))
            throw new InvalidDataException("White-listed resource parameter layout differs: " + spec.Name);
        return method;
    }

    private static void VerifyChangeUinScript(AfdevOwnedProcess process, ResourceMethod method, uint uinProperty)
    {
        byte[] expected = new byte[22];

        expected[0] = 0x0F;
        expected[1] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(2, 4), uinProperty);
        expected[10] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(11, 4), method.Parameter!.Identity.Address);
        expected[19] = 0x04;
        expected[20] = 0x0B;
        expected[21] = 0x53;
        if (!process.ReadMemory(method.Script, 22).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("ChangeUin runtime script differs from the reviewed borrowed-input assignment.");
    }
}

sealed record ResourceMethodSpec(uint Ordinal, string Name, uint Flags, uint PropertiesSize, uint ParmsSize, uint ReturnOffset, uint ScriptSize, string? ParameterName, string? ParameterClass, uint ParameterSize, uint ParameterFlags);
sealed record ResourceMethod(ResourceMethodSpec Spec, QueryObject Identity, uint Flags, ushort NativeIndex, uint PropertiesSize, ushort ParmsSize, ushort ReturnOffset, uint Exec, uint Script, uint ScriptCount, uint ScriptCapacity, ResourceCallParameter? Parameter);
sealed record ResourceCallParameter(QueryObject Identity, uint Next, uint Dimension, uint Size, uint Flags, uint HighFlags, uint Offset, uint BoolMask);
sealed record ResourceInitialization(ResourceProfile Profile, bool RecoverReturnedTrue, IReadOnlyList<string> CompletedCalls);
sealed record CommandIdleAckReceipt(uint Before, uint After, DateTimeOffset ObservedAtUtc);
