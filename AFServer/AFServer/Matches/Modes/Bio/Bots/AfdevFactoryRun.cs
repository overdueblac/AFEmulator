using System.Buffers.Binary;
using System.Diagnostics;
using AFServer.AssaultFire.Matches;

namespace AFServer.AssaultFire.Matches.Bots;
static partial class FactoryRun
{
    public static async Task<uint> ExecuteAsync(AfdevOwnedProcess process, AfdevNativeEvidence evidence, uint commandAddress, Action<string, object> save, Action<string> report, CancellationToken cancellationToken,
        bool describeOnly = false, uint preparationCommand = 0)
    {
        if (preparationCommand != 0)
            AfdevBioBotPreparationInspector.RequirePreparing(process, evidence, preparationCommand);

        var inspector = new FactoryInspector(process);

        save("factory-before", inspector.Snapshot(evidence));
        save("native-slots", inspector.NativeSlots());

        FactoryFunction function = inspector.Resolve(evidence);
        FactoryLocation location = inspector.FindControllerLocation(evidence);
        var reader = inspector.Reader;
        var proofs = new Dictionary<uint, uint>();
        var guardProperties = new List<uint>();
        uint parms = commandAddress + 0xC00;
        byte[] command = new byte[FactoryHook.CommandSize];
        byte[] parameters = new byte[28];

        location.Location.CopyTo(parameters, 0);
        location.Rotation.CopyTo(parameters, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(24, 4), uint.MaxValue);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(offset, 4), value);

        uint GuardField(uint instance, string path, string kind = "IntProperty", uint size = 4)
        {
            uint address = reader.FieldAddress(instance, path.Split('.')[^1], path, kind, size);
            uint property = reader.FindField(reader.U32(instance + 0x34), path.Split('.')[^1], path);

            guardProperties.Add(property);

            foreach (uint offset in new uint[] { 0x28, 0x34, 0x40, 0x44, 0x60 })
                proofs.TryAdd(property + offset, reader.U32(property + offset));

            return address;
        }

        void GuardBit(int slot, string path)
        {
            uint address = GuardField(evidence.GameInfo, path, "BoolProperty");
            uint property = guardProperties[^1];
            uint mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("Factory waiting/warmup bit is not a single native flag.");

            proofs.TryAdd(property + 0x84, mask);
            Set(slot, address);
            Set(slot + 4, mask);
        }

        uint frame = reader.U32(evidence.GameInfo + 0x18);
        uint pending = frame == 0 ? 0 : reader.U32(frame + 0x28);

        if (pending == 0 || reader.Path(pending) != "UTGame.TGGame.PendingMatch")
            throw new InvalidDataException("Factory requires the exact PendingMatch state.");

        Set(0x780, pending);
        Set((int)FactoryHook.PreparationPage, preparationCommand);

        int guardSlot = 0x784;

        foreach (string name in new[] { "NumPlayers", "NumBots", "NumTravellingPlayers", "NumSpectators" })
        {
            Set(guardSlot, GuardField(evidence.GameInfo, "Engine.GameInfo." + name));
            guardSlot += 4;
        }

        Set(0x794, GuardField(evidence.GameInfo, "UTGame.UTGame.MinNetPlayers"));
        GuardBit(0x798, "Engine.GameInfo.bWaitingToStartMatch");
        GuardBit(0x7A0, "UTGame.UTGame.bWarmupRound");
        Set(0x7A8, evidence.NetDriver);
        Set(0x7B0, GuardField(evidence.WorldInfo, "Engine.WorldInfo.NetMode", "ByteProperty", 1));
        Set(0x7AC, (uint)proofs.Count);

        if (proofs.Count > 64)
            throw new InvalidDataException("Factory preparation metadata exceeds its fixed proof budget.");

        int proofSlot = 0x900;

        foreach (var proof in proofs)
        {
            Set(proofSlot, proof.Key);
            Set(proofSlot + 4, proof.Value);
            proofSlot += 8;
        }

        IReadOnlyList<QueryObject> identities = IdentityClosure(reader, [evidence.World, evidence.WorldInfo,
            evidence.GameInfo, pending, function.Function, function.ControllerClass, function.PriClass, location.Actor,
            ..guardProperties, ..function.Properties.SelectMany(item => new[] { item.Address, item.Type })]);
        QueryObject fn = identities.Single(item => item.Address == function.Function);

        if (identities.Count > 64)
            throw new InvalidDataException("Factory object identity closure exceeds its fixed proof budget.");

        Set(0x40, process.MainThreadId);
        Set(0x44, inspector.Reader.U32(0x02063820));
        Set(0x48, evidence.World);
        Set(0x4C, evidence.WorldInfo);
        Set(0x50, evidence.GameInfo);
        Set(0x54, evidence.GameInfoClass);
        Set(0x58, function.Function);
        Set(0x5C, parms);
        Set(0x60, function.FunctionClass);
        Set(0x64, function.FunctionOwner);
        Set(0x80, function.ExecPointer);
        Set(0x84, function.ScriptPointer);
        Set(0x88, function.ScriptCount);
        Set(0x8C, function.ScriptCapacity);
        Set(0x90, function.Properties[0].Address);
        Set(0xA0, fn.NameIndex);
        Set(0xA4, fn.NameNumber);
        Set(0xA8, fn.Index);
        Set(0xC4, (uint)identities.Count);
        Set(0xE0, function.ControllerClassAddress);
        Set(0xE4, function.ControllerClass);
        Set(0xE8, function.PriClassAddress);
        Set(0xEC, function.PriClass);

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

        for (int i = 0; i < 3; i++)
        {
            FactoryProperty property = function.Properties[i];
            uint[] values = [property.Address, property.Next, property.Dimension, property.Size, property.Flags, property.HighFlags, property.Offset, property.Type];

            for (int j = 0; j < values.Length; j++)
                Set(0x700 + i * 32 + j * 4, values[j]);
        }

        save("factory-layout", new ExecuteAsyncJson1 { function = function, location = location, identities = identities, commandAddress = commandAddress, parms = parms, ParametersHex = Convert.ToHexString(parameters), ScriptHex = Convert.ToHexString(process.ReadMemory(function.ScriptPointer, (int)function.ScriptCount)), ScriptSha256 = TickHook.Hash(process.ReadMemory(function.ScriptPointer, (int)function.ScriptCount)), CommandHex = Convert.ToHexString(command) });

        if (describeOnly)
        {
            save("factory-descriptor-budget", new ExecuteAsyncJson2 { IdentityCount = identities.Count, IdentityEnd = 0x100 + identities.Count * 24,
                PropertyStart = 0x700, ProofCount = proofs.Count, ProofEnd = 0x900 + proofs.Count * 8,
                CommandSize = FactoryHook.CommandSize, ParameterStart = 0xC00, Published = false });
            return 0;
        }

        process.WriteMemory(commandAddress, command);
        process.WriteMemory(parms, parameters);
        if (!process.ReadMemory(commandAddress, command.Length).AsSpan().SequenceEqual(command) || !process.ReadMemory(parms, parameters.Length).AsSpan().SequenceEqual(parameters))
            throw new InvalidDataException("Unarmed factory command/parameters readback differs.");
        report("FACTORY_ARMED exact Engine.GameInfo.SpawnPlayerController, one unconnected BIO Controller/PRI only");

        var observations = new List<QueryObservation>();

        try
        {
            process.WriteMemory(commandAddress, [1, 0, 0, 0]);

            var timer = Stopwatch.StartNew();

            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.IsAlive)
                    throw new IOException("Owned process exited during factory; no retry.");

                QueryObservation observation = QueryRun.Read(process, commandAddress, parms + 24);

                observations.Add(observation);
                if (observation.Complete == 1)
                {
                    uint controller = unchecked((uint)observation.Result);

                    if (observation.Started != 1 || observation.Status != 1 || controller < 0x10000 || controller == uint.MaxValue || observation.Result != observation.ParmsValue || observation.ThreadBefore != process.MainThreadId || observation.ThreadAfter != process.MainThreadId || observation.StackBefore == 0 || observation.StackBefore != observation.StackAfter || observation.WorldBefore != evidence.World || observation.WorldAfter != evidence.World || observation.GameBefore != evidence.GameInfo || observation.GameAfter != evidence.GameInfo || observation.ClassBefore != evidence.GameInfoClass || observation.ClassAfter != evidence.GameInfoClass)
                        throw new InvalidDataException("Completed factory evidence differs from exact BIO session.");
                    save("factory-created", inspector.Snapshot(evidence, controller));
                    report("FACTORY_PASS unconnected BIO Controller/PRI created; no Pawn, not a legal Bot spawn yet");
                    return controller;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("Factory deadline elapsed; unknown/blocked invocation is not retried.");
        }
        finally
        {
            save("factory-observations", observations);
            if (process.IsAlive)
                save("factory-final", new ExecuteAsyncJson3 { Observation = QueryRun.Read(process, commandAddress, parms + 24), CommandHex = Convert.ToHexString(process.ReadMemory(commandAddress, FactoryHook.CommandSize)) });
        }
    }

    /// <summary>
    /// Only factory-command 0x100..0x700 supports 64 entries. Other native commands retain their original
    /// 32-entry limits.
    /// </summary>
    private static IReadOnlyList<QueryObject> IdentityClosure(QueryInspector reader, params uint[] roots)
    {
        var result = new List<QueryObject>();
        var pending = new Queue<uint>(roots);
        var seen = new HashSet<uint>();

        while (pending.TryDequeue(out uint pointer))
        {
            if (pointer == 0 || !seen.Add(pointer))
                continue;
            if (seen.Count > 64)
                throw new InvalidDataException("Factory identity closure exceeds its independent 64-object region.");

            QueryObject item = reader.Identity(pointer);

            result.Add(item);
            pending.Enqueue(item.Outer);
            pending.Enqueue(item.Class);
        }

        return result;
    }
}
