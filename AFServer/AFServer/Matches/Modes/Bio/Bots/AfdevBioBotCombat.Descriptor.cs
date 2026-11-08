using System.Buffers.Binary;
using System.Text;

namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotCombat
{
    private byte[] Describe(AfdevBioBotCombatAction action, AfdevBioBotCombatSnapshot expected, uint targetPawn, uint next,
        AfdevBioBotPoint? destination)
    {
        uint pc = participant.Controller;
        uint pri = participant.Pri;
        uint pawn = expected.Pawn.Address;
        uint weapon = expected.Weapon.Address;
        CombatFunction call = functions.Describe(action, pawn);
        ResourceFunction method = call.Function;
        byte[] payload = new byte[AfdevBioBotCombatHook.PayloadSize];
        var proofs = new Dictionary<(uint Address, uint Mask), uint>();
        var roots = new HashSet<uint>(call.Roots) { pc, pri, pawn, weapon, world.WorldInfo, world.GameInfo };

        void Put(uint offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan((int)offset, 4), value);
        void Prove(uint address, uint value, uint mask = uint.MaxValue)
        {
            if ((reader.U32(address) & mask) != value)
                throw new AfdevBioBotActionInterruptedException("战斗描述期间对象、装备或回合已变化。");

            if (proofs.TryGetValue((address, mask), out uint previous) && previous != value)
                throw new InvalidDataException("战斗命令同一字段的证明冲突。");

            proofs[(address, mask)] = value;
        }

        void Bytes(uint address, byte[] bytes)
        {
            for (int offset = 0; offset < bytes.Length; offset += 4)
            {
                int length = Math.Min(4, bytes.Length - offset);
                uint value = 0;

                for (int index = 0; index < length; index++)
                    value |= (uint)bytes[offset + index] << (8 * index);

                Prove(address + (uint)offset, value, length == 4 ? uint.MaxValue : (1u << (8 * length)) - 1);
            }
        }

        uint Property(uint instance, string path, string kind = "ObjectProperty", uint size = 4)
        {
            uint property = reader.FindField(reader.Identity(instance).Class, path[(path.LastIndexOf('.') + 1)..], path);
            uint address = Field(instance, path, kind, size);

            roots.Add(instance);
            roots.Add(property);
            Prove(property + 0x40, 1);
            Prove(property + 0x44, size);
            Prove(property + 0x60, address - instance);

            if (kind is "ObjectProperty" or "ClassProperty" or "ComponentProperty" or "StructProperty")
            {
                uint type = reader.U32(property + 0x84);

                roots.Add(type);
                Prove(property + 0x84, type);
            }

            return address;
        }

        void Boolean(uint instance, string path, bool value)
        {
            uint address = Property(instance, path, "BoolProperty");
            uint property = reader.FindField(reader.Identity(instance).Class, path[(path.LastIndexOf('.') + 1)..], path);
            uint mask = reader.U32(property + 0x84);

            if (mask == 0 || (mask & (mask - 1)) != 0)
                throw new InvalidDataException("战斗布尔字段不是已反射的单个位。");

            Prove(property + 0x84, mask);
            Prove(address, value ? mask : 0, mask);
        }

        void Actor(uint actor)
        {
            roots.Add(actor);
            Prove(Property(actor, "Engine.Actor.WorldInfo"), world.WorldInfo);
            Prove(actor + 0xB0, 0, 8);
        }

        if (reader.Identity(pawn) != expected.Pawn || reader.Identity(weapon) != expected.Weapon ||
            !reader.IsA(pc, "TGBioGame.TGBioPlayerController") || !reader.IsA(pawn, "TGBioGame.TGBioPawn") ||
            !reader.IsA(weapon, "UTGame.TGRealWeapon"))
            throw new AfdevBioBotActionInterruptedException("战斗入口不再属于原 BIO Pawn 和武器。");

        foreach (uint actor in new[] { pc, pri, pawn, weapon })
            Actor(actor);

        Prove(Property(pc, "Engine.Controller.PlayerReplicationInfo"), pri);
        Prove(Property(pri, "Engine.Actor.Owner"), pc);
        Prove(Property(pc, "Engine.PlayerController.Player"), 0);
        Prove(Property(pc, "Engine.Controller.Pawn"), pawn);
        Prove(Property(pawn, "Engine.Pawn.Controller"), pc);
        Prove(Property(pawn, "Engine.Pawn.PlayerReplicationInfo"), pri);
        Prove(Property(pawn, "Engine.Pawn.Weapon"), weapon);
        Prove(Property(weapon, "Engine.Actor.Instigator"), pawn);
        Prove(Property(pawn, "Engine.Actor.Role", "ByteProperty", 1), 3, 0xFF);
        Boolean(pri, "Engine.PlayerReplicationInfo.bBot", true);
        Boolean(pc, "Engine.Controller.bIsRobot", true);

        uint inventory = reader.U32(Property(pawn, "Engine.Pawn.InvManager"));
        uint team = reader.U32(Property(pri, "Engine.PlayerReplicationInfo.Team"));

        if (inventory < 0x10000 || team < 0x10000)
            throw new AfdevBioBotActionInterruptedException("原生装备管理器或阵营尚未稳定。");

        uint teamIndex = reader.U32(Property(team, "Engine.TeamInfo.TeamIndex", "IntProperty"));

        Actor(inventory);
        Prove(Property(pawn, "Engine.Pawn.InvManager"), inventory);
        Prove(Property(weapon, "Engine.Inventory.InvManager"), inventory);
        Prove(Property(inventory, "Engine.Actor.Instigator"), pawn);
        Prove(Property(pri, "Engine.PlayerReplicationInfo.Team"), team);
        Prove(Property(team, "Engine.TeamInfo.TeamIndex", "IntProperty"), teamIndex);

        if (action != AfdevBioBotCombatAction.StopFire)
        {
            if (teamIndex != expected.TeamIndex || Int(Property(pawn, "Engine.Pawn.Health", "IntProperty")) <= 0)
                throw new AfdevBioBotActionInterruptedException("战斗意图的阵营或存活状态已变化。");

            Boolean(world.GameInfo, "UTGame.TGRoundMatch.bInRound", true);
            Boolean(world.GameInfo, "UTGame.TGRoundMatch.bCanMove", true);

            uint frame = reader.U32(pc + 0x18);
            uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);

            if (state == 0 || !reader.Path(state).EndsWith(".PlayerWalking", StringComparison.Ordinal))
                throw new AfdevBioBotActionInterruptedException("原 BIO 控制器尚不处于可交战状态。");

            roots.Add(state);
            Prove(pc + 0x18, frame);
            Prove(frame + 0x28, state);
        }

        Put(0x98, Property(pawn, "Engine.Pawn.Health", "IntProperty"));

        if (targetPawn != 0)
        {
            if (action is AfdevBioBotCombatAction.StopFire or AfdevBioBotCombatAction.Reload
                or AfdevBioBotCombatAction.FaceMovement or AfdevBioBotCombatAction.FaceDestination
                or AfdevBioBotCombatAction.SwitchToBestWeapon or AfdevBioBotCombatAction.StartSkill
                or AfdevBioBotCombatAction.BecomeHero)
                throw new ArgumentException("停火、换装备和移动转向不应绑定一个旧敌对目标。", nameof(targetPawn));
            if (targetPawn == pawn || !reader.IsA(targetPawn, "Engine.Pawn") || Deleted(targetPawn))
                throw new AfdevBioBotActionInterruptedException("战斗目标已经退休。");

            uint targetController = reader.U32(Property(targetPawn, "Engine.Pawn.Controller"));
            uint targetPri = reader.U32(Property(targetPawn, "Engine.Pawn.PlayerReplicationInfo"));

            if (targetController < 0x10000 || targetPri < 0x10000 || targetController == pc)
                throw new AfdevBioBotActionInterruptedException("战斗目标已不再持有原生控制器或 PRI。");

            uint targetTeam = reader.U32(Property(targetPri, "Engine.PlayerReplicationInfo.Team"));

            if (targetTeam < 0x10000)
                throw new AfdevBioBotActionInterruptedException("战斗目标没有有效阵营。");

            uint targetTeamIndex = reader.U32(Property(targetTeam, "Engine.TeamInfo.TeamIndex", "IntProperty"));
            uint targetHealth = Property(targetPawn, "Engine.Pawn.Health", "IntProperty");

            if (targetTeamIndex == teamIndex || targetTeamIndex > 1 || teamIndex > 1 || Int(targetHealth) <= 0)
                throw new AfdevBioBotActionInterruptedException("目标已死亡、感染或不再敌对。");

            foreach (uint actor in new[] { targetPawn, targetController, targetPri })
                Actor(actor);

            Boolean(world.GameInfo, "TGBioGame.TGBioMatch.bRoundStarted", true);
            Prove(Property(targetController, "Engine.Controller.Pawn"), targetPawn);
            Prove(Property(targetController, "Engine.Controller.PlayerReplicationInfo"), targetPri);
            Prove(Property(targetPri, "Engine.Actor.Owner"), targetController);
            Prove(Property(targetPawn, "Engine.Pawn.Controller"), targetController);
            Prove(Property(targetPawn, "Engine.Pawn.PlayerReplicationInfo"), targetPri);
            Prove(Property(targetPri, "Engine.PlayerReplicationInfo.Team"), targetTeam);
            Prove(Property(targetTeam, "Engine.TeamInfo.TeamIndex", "IntProperty"), targetTeamIndex);
            Put(0x9C, targetHealth);
        }
        else if (action == AfdevBioBotCombatAction.LineOfSight)
        {
            throw new ArgumentException("原生视线查询需要当前有效目标。", nameof(targetPawn));
        }

        if (action is AfdevBioBotCombatAction.StartSkill or AfdevBioBotCombatAction.BecomeHero)
        {
            Boolean(world.GameInfo, "TGBioGame.TGBioMatch.bRoundStarted", true);

            string requiredPawn = action == AfdevBioBotCombatAction.StartSkill
                ? "TGBioGame.TGBioZombiePawn" : "TGBioGame.TGBioPlayerPawn";

            if (!reader.IsA(pawn, requiredPawn))
                throw new AfdevBioBotActionInterruptedException("技能或英雄请求的原生角色类型已变化。");
        }

        uint function = method.Function.Address;

        foreach ((uint offset, uint value, uint mask) in new[]
        {
            (0x50u, method.PropertiesSize, uint.MaxValue), (0x54u, method.ScriptPointer, uint.MaxValue),
            (0x58u, method.ScriptCount, uint.MaxValue), (0x5Cu, method.ScriptCapacity, uint.MaxValue),
            (0xACu, method.Flags, uint.MaxValue), (0xB0u, 0u, 0xFFFFu),
            (0xBEu, (uint)method.ParmsSize, 0xFFFFu), (0xC0u, (uint)method.ReturnOffset, 0xFFFFu),
            (0xC8u, method.ExecPointer, uint.MaxValue),
            (0x4Cu, method.Parameters.FirstOrDefault()?.Identity.Address ?? 0, uint.MaxValue)
        })
            Prove(function + offset, value, mask);

        for (int index = 0; index < method.Parameters.Count; index++)
        {
            ResourceParameter parameter = method.Parameters[index];
            uint property = parameter.Identity.Address;

            roots.Add(property);
            Prove(property + 0x3C, index + 1 == method.Parameters.Count ? 0 : method.Parameters[index + 1].Identity.Address);
            Prove(property + 0x40, 1);
            Prove(property + 0x44, parameter.Size);
            Prove(property + 0x48, parameter.Flags);
            Prove(property + 0x4C, 0);
            Prove(property + 0x60, parameter.Offset);

            if (parameter.Kind is "ObjectProperty" or "StructProperty" or "BoolProperty")
            {
                Prove(property + 0x84, parameter.TypeOrBoolMask);

                if (parameter.Kind != "BoolProperty")
                    roots.Add(parameter.TypeOrBoolMask);
                else if (parameter.TypeOrBoolMask != 1)
                    throw new InvalidDataException("战斗返回 Bool 参数不是已核的 mask 1。");
            }
        }

        foreach ((uint address, uint value, uint mask) in call.NativeProofs)
            Prove(address, value, mask);

        Bytes(method.ScriptPointer, call.Script);
        Prove(method.Receiver.Address, reader.U32(method.Receiver.Address));
        Prove(reader.U32(method.Receiver.Address) + 0x120, QueryHook.ActorProcessEvent);

        uint uin = Property(pri, "UTGame.TGPlayerReplicationInfo.Uin", "StrProperty", 12);
        uint uinData = reader.U32(uin);
        int capacity = Int(uin + 8);
        byte[] uinBytes = Encoding.Unicode.GetBytes(participant.Uin + '\0');

        if (participant.Uin.Length != 16 || Int(uin + 4) != 17 || capacity is < 17 or > 1024 ||
            uinData < 0x10000 || botId == Guid.Empty || onlinePage < 0x10000 ||
            !process.ReadMemory(uinData, uinBytes.Length).AsSpan().SequenceEqual(uinBytes))
            throw new InvalidDataException("战斗入口缺少完整内部身份或合法 FString。");

        Prove(uin, uinData);
        uinBytes.CopyTo(payload, (int)AfdevBioBotCombatHook.Uin);
        Put(0x90, uin);
        Put(0x94, (uint)capacity);

        Prove(onlinePage, 1);
        Prove(onlinePage + 4, 0);
        Prove(onlinePage + 0x40, world.World);
        Prove(onlinePage + 0x44, world.WorldInfo);
        Prove(onlinePage + 0x48, world.GameInfo);
        Prove(onlinePage + 0x4C, pc);
        Prove(onlinePage + 0x50, pri);
        Bytes(onlinePage + 0x100, participant.InstanceId.ToByteArray());
        Bytes(onlinePage + 0x110, participant.AttemptId.ToByteArray());
        Bytes(onlinePage + 0x120, participant.AllocationId.ToByteArray());
        Bytes(onlinePage + 0x130, botId.ToByteArray());
        Prove(onlinePage + 0x140, unchecked((uint)participant.WorldGeneration));
        Prove(onlinePage + 0x144, unchecked((uint)(participant.WorldGeneration >> 32)));
        Bytes(onlinePage + 0x200, uinBytes);

        if (action is AfdevBioBotCombatAction.FaceMovement or AfdevBioBotCombatAction.FaceDestination)
        {
            float[] direction;

            if (destination is { } point)
            {
                float[] location = Location(pawn);

                // Adjacent coordinate segments may finish before the next combat sample. Never reuse Velocity left after the previous segment stopped.
                // Construct only facing arguments for the current waypoint. Native entry points retain movement, collision, and body synchronization.
                direction = [point.X - location[0], point.Y - location[1], 0];
            }
            else
            {
                float[] velocity = ReadVector(pawn, "Engine.Actor.Velocity");
                float[] acceleration = ReadVector(pawn, "Engine.Actor.Acceleration");

                direction = velocity[0] * velocity[0] + velocity[1] * velocity[1] >= 25 * 25 ? velocity : acceleration;
            }

            uint rotation = Property(pawn, "Engine.Actor.Rotation", "StructProperty", 12);

            process.ReadMemory(rotation, 12).CopyTo(payload, (int)AfdevBioBotCombatHook.Parameters);

            if (direction[0] * direction[0] + direction[1] * direction[1] >= 1)
            {
                // Update controller facing; native UpdateRotation synchronizes the body. Turning only the body would be overwritten by old view rotation each frame.
                // Direction comes from actual movement or the current waypoint, without changing engine velocity, collision, or paths.
                Put(AfdevBioBotCombatHook.Parameters, 0);
                Put(AfdevBioBotCombatHook.Parameters + 4,
                    unchecked((uint)(int)Math.Round(Math.Atan2(direction[1], direction[0]) * 32768 / Math.PI)));
            }
        }
        else if (action is AfdevBioBotCombatAction.Aim or AfdevBioBotCombatAction.FaceTarget)
        {
            if (targetPawn == 0)
            {
                process.ReadMemory(Property(pc, "Engine.Actor.Rotation", "StructProperty", 12), 12)
                    .CopyTo(payload, (int)AfdevBioBotCombatHook.Parameters);
            }
            else
            {
                float[] source = Location(pawn);
                float[] target = Location(targetPawn);
                float sourceEye = Float(Field(pawn, "Engine.Pawn.BaseEyeHeight", "FloatProperty"));
                float targetEye = Float(Field(targetPawn, "Engine.Pawn.BaseEyeHeight", "FloatProperty"));
                double x = target[0] - source[0];
                double y = target[1] - source[1];
                double z = target[2] + targetEye * 0.65 - source[2] - sourceEye;

                if (!double.IsFinite(z) || !float.IsFinite(sourceEye) || !float.IsFinite(targetEye))
                    throw new InvalidDataException("原生瞄准观察含无效眼高。");

                Put(AfdevBioBotCombatHook.Parameters, unchecked((uint)(int)Math.Round(Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 32768 / Math.PI)));
                Put(AfdevBioBotCombatHook.Parameters + 4, unchecked((uint)(int)Math.Round(Math.Atan2(y, x) * 32768 / Math.PI)));
            }
        }
        else if (action == AfdevBioBotCombatAction.LineOfSight)
        {
            Put(AfdevBioBotCombatHook.Parameters, targetPawn);
            Put(AfdevBioBotCombatHook.Parameters + 4, uint.MaxValue);
        }

        var identities = new List<QueryObject>();
        var pending = new Queue<uint>(roots);
        var seen = new HashSet<uint>();

        while (pending.TryDequeue(out uint address))
        {
            if (address == 0 || !seen.Add(address))
                continue;
            if (seen.Count > AfdevBioBotCombatHook.IdentityCapacity)
                throw new InvalidDataException("战斗命令对象闭包超过固定容量。");

            QueryObject identity = reader.Identity(address);

            identities.Add(identity);
            pending.Enqueue(identity.Outer);
            pending.Enqueue(identity.Class);
        }

        if (identities.Count > AfdevBioBotCombatHook.IdentityCapacity || proofs.Count > AfdevBioBotCombatHook.ProofCapacity)
            throw new InvalidDataException("战斗命令证明超过固定页容量。");

        Put(0x10, uint.MaxValue);
        Put(0x40, process.MainThreadId);
        Put(0x44, reader.U32(0x02063820));
        Put(0x48, world.World);
        Put(0x4C, world.WorldInfo);
        Put(0x50, world.GameInfo);
        Put(0x54, pc);
        Put(0x58, pri);
        Put(0x5C, pawn);
        Put(0x60, weapon);
        Put(0x64, function);
        Put(0x68, method.Receiver.Address);
        Put(0x78, (uint)identities.Count);
        Put(0x7C, (uint)proofs.Count);
        Put(0x80, next);
        Put(0x84, (uint)action);

        uint cursor = AfdevBioBotCombatHook.IdentityStart;

        foreach (QueryObject identity in identities)
            foreach (uint value in new[] { identity.Address, identity.Index, identity.Outer, identity.NameIndex, identity.NameNumber, identity.Class })
            {
                Put(cursor, value);
                cursor += 4;
            }

        cursor = AfdevBioBotCombatHook.ProofStart;

        foreach (((uint address, uint mask), uint value) in proofs)
        {
            Put(cursor, address);
            Put(cursor + 4, value);
            Put(cursor + 8, mask);
            cursor += 12;
        }

        save("combat-prepared-" + next % 8, new DescribeJson1 { next = next, action = action, targetPawn = targetPawn, Pawn = expected.Pawn, Weapon = expected.Weapon,
            PawnGeneration = expected.PawnGeneration, WeaponGeneration = expected.WeaponGeneration, Function = method.Path,
            Identities = identities.Count, Proofs = proofs.Count });
        return payload;
    }

    private float[] ReadVector(uint actor, string path)
    {
        uint address = Field(actor, path, "StructProperty", 12);
        float[] vector = [Float(address), Float(address + 4), Float(address + 8)];

        if (vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("朝向采样包含非有限运动向量。");

        return vector;
    }
}
