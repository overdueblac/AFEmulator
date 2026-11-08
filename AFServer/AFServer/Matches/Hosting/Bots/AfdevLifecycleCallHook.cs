using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
// One constrained call body. The host may publish only thirteen validated lifecycle methods; read-only descriptors generate identity/metadata assertions.
static class LifecycleCallHook
{
    public const uint PreparationPage = 0xF8;
    public const int CommandSize = 0xD00;
    public const uint CompletedOrdinal = 0xF00;
    public const uint IdleAck = 0xF04;
    public const uint CompletedOperations = 0xF08;
    // Used only by LoadDefaultInventory, mutually exclusive with identity-allocation count/ordinal slots.
    public const uint InventoryPolicyOffset = 0x90;
    public const uint InventoryPolicyMask = 0x94;
    public const uint InventoryPolicyBefore = 0xE8;
    public const uint InventoryPolicyAfter = 0xEC;
    public static byte[] Append(byte[] wrapper, uint trace, uint factoryCommand, uint command)
    {
        byte[] tail = TickHook.Build(0, trace).AsSpan(0xBC).ToArray();
        int insertion = wrapper.Length - tail.Length;

        if (insertion < 0 || !wrapper.AsSpan(insertion).SequenceEqual(tail))
            throw new InvalidDataException("Lifecycle requires the reviewed post-tick tail.");

        var a = new Assembly();

        uint C(uint offset) => command + offset;
        void Load(uint address)
        {
            a.Hex("A1");
            a.Word(address);
        }

        void Store(uint address)
        {
            a.Hex("A3");
            a.Word(address);
        }

        void Set(uint address, uint value)
        {
            a.Hex("C705");
            a.Word(address);
            a.Word(value);
        }

        void Cmp(uint address, uint value)
        {
            a.Hex("813D");
            a.Word(address);
            a.Word(value);
        }

        void Eq(uint address, string failure)
        {
            a.Hex("3B05");
            a.Word(address);
            a.Jcc(0x85, failure);
        }

        void Ne(string failure) => a.Jcc(0x85, failure);
        void Nonzero(string failure)
        {
            a.Hex("85C0");
            a.Jcc(0x84, failure);
        }

        void Thread(string failure, uint offset)
        {
            a.Hex("FF15");
            a.Word(0x019F6228);
            Store(C(offset));
            Eq(C(0x40), failure);
            Load(0x01FE22C0);
            Eq(C(0x40), failure);
            Cmp(0x01FE22C4, 1);
            Ne(failure);
        }

        void World(string failure, uint offset)
        {
            foreach (uint address in new uint[]
            {
                0x01FE229C,
                0x01FD69E0,
                0x02053E50,
                0x02053E58
            }

            )
            {
                Cmp(address, 0);
                Ne(failure);
            }

            Load(0x02063820);
            Eq(C(0x44), failure);
            a.Hex("83B87806000000");
            Ne(failure);
            Load(0x02066BF8);
            Store(C(offset));
            Eq(C(0x48), failure);
        }

        void Identities(string failure)
        {
            Load(C(0x88));
            a.Hex("83F840");
            a.Jcc(0x87, failure);
            Nonzero(failure);
            a.Hex("89C7BE");
            a.Word(C(0x100));

            string loop = "ids_" + failure;

            a.Label(loop);
            a.Hex("8B06");
            Nonzero(failure);
            a.Hex("8B560485D2");
            a.Jcc(0x88, failure);
            a.Hex("3B15");
            a.Word(0x02053EAC);
            a.Jcc(0x8D, failure);
            a.Hex("8B0D");
            a.Word(0x02053EA8);
            a.Hex("85C9");
            a.Jcc(0x84, failure);
            a.Hex("390491");
            Ne(failure);
            foreach (var f in new[]
            {
                (4, 4),
                (0x28, 8),
                (0x2C, 12),
                (0x30, 16),
                (0x34, 20)
            }

            )
            {
                a.Hex("8B50");
                a.Hex(f.Item1.ToString("X2"));
                a.Hex("3B56");
                a.Hex(f.Item2.ToString("X2"));
                Ne(failure);
            }

            a.Hex("F7400C00000020");
            Ne(failure);
            a.Hex("83C6184F");
            Ne(loop);
        }

        void Pair(string failure, uint gameOffset, uint classOffset)
        {
            Load(C(0x48));
            a.Hex("8B4050");
            Nonzero(failure);
            a.Hex("83784000");
            a.Jcc(0x8E, failure);
            a.Hex("8B403C");
            Nonzero(failure);
            a.Hex("8B00");
            Eq(C(0x4C), failure);
            a.Hex("8B8014040000");
            Store(C(gameOffset));
            Eq(C(0x50), failure);
            a.Hex("8B4034");
            Store(C(classOffset));
            Eq(C(0x54), failure);
            foreach (var link in new[]
            {
                (0xC0u, 0x74u),
                (0xC4u, 0x70u),
                (0xC8u, 0x4Cu),
                (0xCCu, 0x4Cu)
            }

            )
            {
                Load(C(link.Item1));
                a.Hex("8B00");
                Eq(C(link.Item2), failure);
            }

            Load(C(0xD0));
            a.Hex("833800");
            Ne(failure); // Every call requires a factory PC without a Player connection.
            foreach (uint pointer in new uint[]
            {
                0x50,
                0x70,
                0x74
            }

            )
            {
                Load(C(pointer));
                a.Hex("F780B000000008000000");
                Ne(failure);
            }

            Load(C(0x60));
            a.Hex("8B4034");
            Eq(C(0x64), failure);
            Cmp(C(PreparationPage), 0); a.Jcc(0x84, "preparation_checked_" + failure);
            a.Bytes(AfdevBioBotPreparationHook.BuildGuard(C(PreparationPage), C(0x48), C(0x50)));
            Nonzero(failure);
            a.Label("preparation_checked_" + failure);
        }

        void Proofs(string failure)
        {
            Load(C(0x84));
            a.Hex("83F868");
            a.Jcc(0x87, failure);
            Nonzero(failure);
            a.Hex("89C7BE");
            a.Word(C(0x800));

            string loop = "proofs_" + failure;

            a.Label(loop);
            a.Hex("8B068B002346083B4604");
            Ne(failure);
            a.Hex("83C60C4F");
            Ne(loop);
        }

        Cmp(C(0), 1);
        a.Jcc(0x84, "requested");
        a.Hex("F0FF05");
        a.Word(C(IdleAck));
        a.Jmp("done");
        a.Label("requested");
        Cmp(C(4), 0);
        Ne("done");
        Cmp(C(8), 0);
        Ne("done");
        Load(C(CompletedOrdinal));
        a.Hex("83F80D");
        a.Jcc(0x83, "sequence");
        a.Hex("40");
        Eq(C(0x80), "sequence");
        Load(C(0x7C));
        a.Hex("48");
        a.Hex("83F80C");
        a.Jcc(0x87, "sequence");
        a.Hex("89C1B801000000D3E0");
        Eq(C(0xB0), "sequence");
        a.Hex("8505");
        a.Word(C(CompletedOperations));
        Ne("sequence");
        Cmp(factoryCommand + 8, 1);
        Ne("sequence");
        Cmp(factoryCommand + 12, 1);
        Ne("sequence");
        Load(factoryCommand + 0x30);
        Eq(C(0x70), "sequence");
        Thread("thread", 0x18);
        World("world", 0x20);
        Cmp(trace + 0x28, 2);
        Ne("world");
        Load(trace + 8);
        Eq(C(0x48), "world");
        Identities("identity");
        Pair("identity", 0x28, 0x38);
        Proofs("metadata");
        // The fixed operation determines the target; commands cannot place arbitrary UObjects in ECX.
        Cmp(C(0x7C), 13);
        a.Jcc(0x84, "pri_target");
        Cmp(C(0x7C), 1);
        a.Jcc(0x84, "pc_target");
        Cmp(C(0x7C), 3);
        a.Jcc(0x84, "pc_target");
        Cmp(C(0x7C), 6);
        a.Jcc(0x84, "pc_target");
        Cmp(C(0x7C), 7);
        a.Jcc(0x84, "pc_target");
        Cmp(C(0x7C), 8);
        a.Jcc(0x84, "pc_target");
        Cmp(C(0x7C), 9);
        a.Jcc(0x84, "manager_target");
        Cmp(C(0x7C), 10);
        a.Jcc(0x84, "manager_target");
        Load(C(0x60));
        Eq(C(0x50), "metadata");
        a.Jmp("target_done");
        a.Label("manager_target");
        Load(C(0xD4));
        a.Hex("8B00");
        Eq(C(0x60), "metadata");
        a.Hex("8B50343B15");
        a.Word(C(0xD8));
        Ne("metadata");
        a.Hex("8B0D");
        a.Word(C(0xDC));
        a.Hex("8B14083B15");
        a.Word(C(0x50));
        Ne("metadata");
        a.Hex("8B0D");
        a.Word(C(0xE0));
        a.Hex("8B14083B15");
        a.Word(C(0x4C));
        Ne("metadata");
        a.Hex("F780B000000008000000");
        Ne("metadata");
        a.Jmp("target_done");
        a.Label("pc_target");
        Load(C(0x60));
        Eq(C(0x70), "metadata");
        a.Jmp("target_done");
        a.Label("pri_target");
        Load(C(0x60));
        Eq(C(0x74), "metadata");
        a.Label("target_done");
        Load(C(0x60));
        a.Hex("8B4018");
        Eq(C(0xA0), "metadata");
        a.Hex("85C0");
        a.Jcc(0x84, "state_done");
        a.Hex("8B4028");
        Eq(C(0xA4), "metadata");
        a.Label("state_done");
        Load(C(0x60));
        a.Hex("8B00");
        a.Hex("81B820010000");
        a.Word(QueryHook.ActorProcessEvent);
        Ne("metadata");
        Load(C(0x98));
        a.Hex("83F834");
        a.Jcc(0x87, "metadata");
        a.Hex("A903000000");
        Ne("metadata");
        a.Hex("C1E80289C1");
        a.Hex("85C0");
        a.Jcc(0x84, "params_done");
        a.Hex("8B35");
        a.Word(C(0x5C));
        a.Hex("8B3D");
        a.Word(C(0x9C));
        a.Label("params_loop");
        a.Hex("8B063B07");
        Ne("metadata");
        a.Hex("83C60483C70449");
        Ne("params_loop");
        a.Label("params_done");
        Cmp(C(0x7C), 5);
        a.Jcc(0x85, "not_spawn");
        Cmp(C(PreparationPage), 0);
        a.Jcc(0x85, "spawn_parameters");
        Load(C(0xD4));
        a.Hex("833800");
        Ne("metadata");
        a.Label("spawn_parameters");
        Load(C(0x5C));
        a.Hex("8B00");
        Eq(C(0xD8), "metadata");
        Load(C(0x5C));
        a.Hex("8B4004");
        Eq(C(0x50), "metadata");
        // Actor.Spawn permits only the fixed BIO manager, current GameInfo, and zero optional arguments.
        a.Hex("8B35");
        a.Word(C(0x5C));
        a.Hex("83C608B90A000000");
        a.Label("spawn_zero");
        a.Hex("833E00");
        Ne("metadata");
        a.Hex("83C60449");
        Ne("spawn_zero");
        a.Label("not_spawn");
        Cmp(C(0x7C), 9);
        a.Jcc(0x85, "not_array");
        Load(C(0x5C));
        a.Hex("8B00");
        a.Hex("3D");
        a.Word(C(0xE00));
        Ne("metadata");
        Load(C(0x5C));
        a.Hex("83780404");
        Ne("metadata");
        a.Hex("83780804");
        Ne("metadata");
        foreach (uint offset in new uint[]
        {
            0,
            4,
            8,
            12
        }

        )
        {
            Load(C(0xE00 + offset));
            Eq(C(0xE40 + offset), "metadata");
        }

        a.Label("not_array");
        Cmp(C(0x7C), 12);
        a.Jcc(0x85, "not_identity");
        Cmp(C(0xE0), 0x01FA8E50);
        Ne("metadata");
        Cmp(C(PreparationPage), 0);
        a.Jcc(0x84, "identity_sequence_legacy");
        // Human PostLogin shares this native ordinal. Capture it within the actual game-thread call, never from a stale managed reading.
        Load(0x01FA8E50);
        Store(C(0xE8));
        a.Jmp("identity_sequence_ready");
        a.Label("identity_sequence_legacy");
        Load(0x01FA8E50);
        Eq(C(0xE8), "metadata");
        a.Label("identity_sequence_ready");
        a.Hex("3DFFFFFF7F");
        a.Jcc(0x83, "metadata");
        Load(C(0xD4));
        a.Hex("8B00");
        Eq(C(0xEC), "metadata");
        Cmp(C(PreparationPage), 0);
        a.Jcc(0x85, "not_identity");
        foreach (uint offset in new uint[]
        {
            0x90,
            0x94
        }

        )
        {
            Load(C(offset));
            a.Hex("833800");
            Ne("metadata");
        }

        a.Label("not_identity");
        Cmp(C(0x7C), 13);
        a.Jcc(0x85, "not_name");
        Load(C(0x5C));
        a.Hex("8B10");
        a.Hex("81FA");
        a.Word(C(0xE00));
        Ne("metadata");
        a.Hex("8B50043B15");
        a.Word(C(0xF4));
        Ne("metadata");
        a.Hex("8B50083B15");
        a.Word(C(0xF4));
        Ne("metadata");
        Load(C(0xF4));
        a.Hex("83F802");
        a.Jcc(0x82, "metadata");
        a.Hex("83F840");
        a.Jcc(0x87, "metadata");
        a.Hex("89C1BE");
        a.Word(C(0xE00));
        a.Hex("BF");
        a.Word(C(0xE80));
        a.Hex("F366A7");
        Ne("metadata");
        a.Label("not_name");
        Load(C(0x8C));
        a.Hex("3DFFFF0000");
        a.Jcc(0x84, "invoke");
        a.Hex("83C004");
        a.Hex("3B05");
        a.Word(C(0x98));
        a.Jcc(0x87, "metadata");
        a.Label("invoke");
        Set(C(0x10), 0);
        Set(C(4), 1);
        a.Hex("8925");
        a.Word(C(0x68));
        Cmp(C(0x7C), 5);
        a.Jcc(0x85, "invoke_native");
        Cmp(C(PreparationPage), 0);
        a.Jcc(0x84, "invoke_native");
        Load(C(0xD4));
        a.Hex("8B0085C0");
        a.Jcc(0x84, "invoke_native");
        // Human PreLogin may create the shared Manager first. Confirm and reuse it on the game thread rather than create a second object.
        Store(C(0x30));
        a.Hex("8B15"); a.Word(C(0x5C));
        a.Hex("0315"); a.Word(C(0x8C));
        a.Hex("89028925"); a.Word(C(0x6C));
        Set(C(0xE4), 2);
        a.Jmp("returned");
        a.Label("invoke_native");
        // Bulk default-equipment loading changes shared bFindAnyWeapon and disables the human all-weapons fallback.
        // Save and restore that bit within the same game-thread call; a pre-read host value must not overwrite human PreLogin changes.
        Cmp(C(0x7C), 9);
        a.Jcc(0x85, "inventory_policy_saved");
        Load(C(0x60));
        a.Hex("0305"); a.Word(C(InventoryPolicyOffset));
        a.Hex("8B002305"); a.Word(C(InventoryPolicyMask));
        Store(C(InventoryPolicyBefore));
        a.Label("inventory_policy_saved");
        a.Hex("6A00FF35");
        a.Word(C(0x5C));
        a.Hex("FF35");
        a.Word(C(0x58));
        a.Hex("8B0D");
        a.Word(C(0x60));
        a.Hex("8B01FF9020010000");
        a.Hex("8925");
        a.Word(C(0x6C));
        Cmp(C(0x7C), 9);
        a.Jcc(0x85, "inventory_policy_restored");
        Load(C(0x60));
        a.Hex("0305"); a.Word(C(InventoryPolicyOffset));
        a.Hex("8B15"); a.Word(C(InventoryPolicyMask));
        a.Hex("F7D221108B15"); a.Word(C(InventoryPolicyBefore));
        a.Hex("09108B002305"); a.Word(C(InventoryPolicyMask));
        Store(C(InventoryPolicyAfter));
        a.Label("inventory_policy_restored");
        Set(C(0x30), 0);
        Load(C(0x8C));
        a.Hex("3DFFFF0000");
        a.Jcc(0x84, "returned");
        a.Hex("8B0D");
        a.Word(C(0x5C));
        a.Hex("8B0401");
        Store(C(0x30));
        a.Label("returned");
        Load(C(0x68));
        Eq(C(0x6C), "changed");
        Thread("changed", 0x1C);
        World("changed", 0x24);
        Identities("changed");
        Pair("changed", 0x2C, 0x3C);
        Proofs("changed");
        Cmp(C(0x7C), 12);
        a.Jcc(0x85, "not_identity_return");
        Load(C(0x30));
        Eq(C(0xE8), "changed");
        a.Hex("40");
        Eq(0x01FA8E50, "changed");
        Cmp(C(PreparationPage), 0);
        a.Jcc(0x85, "identity_counts_checked");
        foreach (uint offset in new uint[]
        {
            0x90,
            0x94
        }

        )
        {
            Load(C(offset));
            a.Hex("833800");
            Ne("changed");
        }

        a.Label("identity_counts_checked");
        Load(C(0xD4));
        a.Hex("8B10");
        a.Hex("3B15");
        a.Word(C(0xEC));
        Ne("changed");
        a.Hex("8B15");
        a.Word(C(0x30));
        a.Hex("8910");
        Load(C(0xD8));
        a.Hex("8B15");
        a.Word(C(0xDC));
        a.Hex("0910");
        Set(C(0xE4), 1);
        a.Jmp("success");
        a.Label("not_identity_return");
        Cmp(C(0x7C), 5);
        a.Jcc(0x85, "success");
        // Validate the new actor within the same AFDEV safety gate before binding the previously empty MyRefInvManager.
        Load(C(0x30));
        a.Hex("3D00000100");
        a.Jcc(0x82, "changed");
        a.Hex("A903000000");
        Ne("changed");
        a.Hex("8B500485D2");
        a.Jcc(0x88, "changed");
        a.Hex("3B15");
        a.Word(0x02053EAC);
        a.Jcc(0x8D, "changed");
        a.Hex("8B0D");
        a.Word(0x02053EA8);
        a.Hex("85C9");
        a.Jcc(0x84, "changed");
        a.Hex("390491");
        Ne("changed");
        a.Hex("F7400C00000020");
        Ne("changed");
        a.Hex("F780B000000008000000");
        Ne("changed");
        a.Hex("8B50343B15");
        a.Word(C(0xD8));
        Ne("changed");
        a.Hex("8B0D");
        a.Word(C(0xDC));
        a.Hex("8B14083B15");
        a.Word(C(0x50));
        Ne("changed");
        a.Hex("8B0D");
        a.Word(C(0xE0));
        a.Hex("8B14083B15");
        a.Word(C(0x4C));
        Ne("changed");
        // Actor-array visibility and object-table identity are separate assertions; both must pass.
        a.Hex("89C7");
        Load(C(0x48));
        a.Hex("8B40508B484085C9");
        a.Jcc(0x8E, "changed");
        a.Hex("8B703C85F6");
        a.Jcc(0x84, "changed");
        a.Label("spawn_actor_loop");
        a.Hex("393E");
        a.Jcc(0x84, "spawn_actor_found");
        a.Hex("83C60449");
        Ne("spawn_actor_loop");
        a.Jmp("changed");
        a.Label("spawn_actor_found");
        Load(C(0xD4));
        Cmp(C(0xE4), 2);
        a.Jcc(0x85, "bind_spawned_manager");
        a.Hex("3938");
        Ne("changed");
        a.Jmp("success");
        a.Label("bind_spawned_manager");
        a.Hex("833800");
        Ne("changed");
        a.Hex("8938");
        Set(C(0xE4), 1);
        a.Label("success");
        Set(C(0x0C), 1);
        Load(C(0x80));
        Store(C(CompletedOrdinal));
        Load(C(0xB0));
        a.Hex("0905");
        a.Word(C(CompletedOperations));
        a.Jmp("complete");
        a.Label("changed");
        Set(C(0x0C), 2);
        a.Label("complete");
        Set(C(8), 1);
        a.Jmp("done");
        foreach (var error in new[]
        {
            ("sequence", 1u),
            ("thread", 2u),
            ("world", 3u),
            ("identity", 4u),
            ("metadata", 5u)
        }

        )
        {
            a.Label(error.Item1);
            Set(C(0x10), error.Item2);
            a.Hex("F0FF05");
            a.Word(C(0x14));
            a.Jmp("done");
        }

        a.Label("done");
        return[..wrapper.AsSpan(0, insertion).ToArray(), ..a.Finish(), ..tail];
    }

    sealed class Assembly
    {
        readonly List<byte> code = [];
        readonly Dictionary<string, int> labels = [];
        readonly List<(int Offset, string Label)> branches = [];
        public void Hex(string hex) => code.AddRange(Convert.FromHexString(hex));
        public void Bytes(byte[] bytes) => code.AddRange(bytes);
        public void Word(uint value)
        {
            byte[] bytes = new byte[4];

            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            code.AddRange(bytes);
        }

        public void Label(string value) => labels.Add(value, code.Count);
        public void Jmp(string label)
        {
            code.Add(0xE9);
            Branch(label);
        }

        public void Jcc(byte condition, string label)
        {
            code.Add(0x0F);
            code.Add(condition);
            Branch(label);
        }

        void Branch(string label)
        {
            branches.Add((code.Count, label));
            Word(0);
        }

        public byte[] Finish()
        {
            byte[] result = code.ToArray();

            foreach (var branch in branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch.Offset, 4), labels[branch.Label] - branch.Offset - 4);
            return result;
        }
    }
}
