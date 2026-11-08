using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Exact script allowlist for ordinary BIO combat. Templates come from verified package-local scripts;
/// rebind UObject/FName live. Never pass numeric native UFunctions directly to ProcessEvent. Skills/heroes
/// submit only player requests subject to native rules.
/// </summary>
internal sealed class AfdevBioBotCombatFunctions(AfdevOwnedProcess process, AfdevNativeEvidence world, uint controller)
{
    private readonly QueryInspector reader = new(process, cacheMetadata: true);
    private readonly ResourceFunctionCatalog catalog = new(process);

    public CombatFunction Describe(AfdevBioBotCombatAction action, uint pawn)
    {
        string path;
        string template;
        string storageHash;
        uint flags;
        uint propertiesSize;
        ushort parmsSize;
        ushort returnOffset = 0xFFFF;
        ResourceParameterExpectation[] parameters;
        (int Offset, string Path)[] references;
        (int Offset, string Name)[] names = [];
        uint receiver = controller;

        switch (action)
        {
            case AfdevBioBotCombatAction.StartSkill:
                path = "TGBioGame.TGBioPlayerController.ClientStartSkill";
                flags = 0x20002;
                propertiesSize = parmsSize = 0;
                parameters = [];
                template = "1b000000000000000016040b53";
                references = [];
                names = [(1, "StartSkill")];
                storageHash = "9a184cab383934781faf7208cc24156ef074ed4c1bb8d0c63bcfde5e0346baac";
                break;
            case AfdevBioBotCombatAction.BecomeHero:
                // Production input retains IsCanTurnToHumanHero; do not use a debug entry that bypasses eligibility checks.
                path = "TGBioGame.TGBioPlayerController.ServerChangeToHumanHero";
                flags = 0x2200C2;
                propertiesSize = 4;
                parmsSize = 0;
                parameters = [new("PRI", "ObjectProperty", 4, 0, 0, "TGBioGame.TGBioPlayerReplicationInfo")];
                template = "0f0000000000000000002e0000000000000000010000000000000000071e0282772e00000000000000000100000000000000002a161837009a19190100000000000000000900000000000000000000010000000000000000090000000000000000000001000000000000000025161607e00182828282771901000000000000000009000000000000000000000100000000000000002a16182b00772e00000000000000001901000000000000000009000000000000000000000100000000000000002a1616180d00770000000000000000002a1616183e00192e00000000000000001901000000000000000009000000000000000000000100000000000000000a000000000000000000002d01000000000000000016183e00192e00000000000000001901000000000000000009000000000000000000000100000000000000000a000000000000000000001b00000000000000001616e770701f417269656c63686f753a204368616e676520506c617965722000385617161f20746f2068756d616e206865726f2100164a16192e000000000000000019010000000000000000090000000000000000000001000000000000000013000000000000000000001b0000000000000000000000000000000000161b000000000000000016061e02e770701f417269656c63686f753a204661696c20746f206368616e676520506c617965722000385617161f20746f2068756d616e206865726f2100164a16040b53";
                references = [(2, path + ".PRI"), (11, "TGBioGame.TGBioPlayerReplicationInfo"),
                    (20, "Engine.Controller.PlayerReplicationInfo"), (34, "TGBioGame.TGBioPlayerPawn"),
                    (43, "Engine.Controller.Pawn"), (60, "Engine.Controller.PlayerReplicationInfo"),
                    (70, "Engine.PlayerReplicationInfo.Team"), (80, "Engine.PlayerReplicationInfo.Team"),
                    (90, "Engine.TeamInfo.TeamIndex"), (100, "Engine.TeamInfo.TeamIndex"),
                    (121, "Engine.Actor.WorldInfo"), (131, "Engine.WorldInfo.Game"), (141, "Engine.WorldInfo.Game"),
                    (156, "TGBioGame.TGBioMatch"), (166, "Engine.Actor.WorldInfo"),
                    (176, "Engine.WorldInfo.Game"), (186, "Engine.WorldInfo.Game"), (202, path + ".PRI"),
                    (218, "TGBioGame.TGBioMatch"), (228, "Engine.Actor.WorldInfo"),
                    (238, "Engine.WorldInfo.Game"), (248, "Engine.WorldInfo.Game"),
                    (258, "TGBioGame.TGBioMatch.bRoundStarted"), (269, "TGBioGame.TGBioMatch.bRoundStarted"),
                    (283, "TGBioGame.TGBioGameReplicationInfo"), (293, "Engine.Actor.WorldInfo"),
                    (303, "Engine.WorldInfo.GRI"), (313, "Engine.WorldInfo.GRI"),
                    (323, "TGBioGame.TGBioGameReplicationInfo.IsCanTurnToHumanHero.ReturnValue"),
                    (399, "TGBioGame.TGBioMatch"), (409, "Engine.Actor.WorldInfo"),
                    (419, "Engine.WorldInfo.Game"), (429, "Engine.WorldInfo.Game"),
                    (458, path + ".PRI")]; // +439 is an empty property reference for a void Context; preserve eight zero bytes.
                names = [(333, "IsCanTurnToHumanHero"), (449, "TurnPlayerToHumanHero"), (468, "OpenSelectHumanHeroWeaponType")];
                storageHash = "280e7a4ed6305c9ec9b7710871515c659a8760664fbe6df19b590fef1c10276d";
                break;
            case AfdevBioBotCombatAction.Aim:
            case AfdevBioBotCombatAction.FaceMovement:
            case AfdevBioBotCombatAction.FaceDestination:
                receiver = pawn;
                path = "Engine.Pawn.SetViewRotation";
                flags = 0x20102;
                propertiesSize = parmsSize = 12;
                parameters = [new("NewRotation", "StructProperty", 12, 0, 0x80, "Core.Object.Rotator")];
                template = "073300770100000000000000002a16190100000000000000000c00000000000000000000612b00000000000000000016063f00612b00000000000000000016040b53";
                references = [(5, "Engine.Pawn.Controller"), (17, "Engine.Pawn.Controller"),
                    (27, "Engine.Actor.SetRotation.ReturnValue"), (39, path + ".NewRotation"), (54, path + ".NewRotation")];
                storageHash = "a4c965203ac24d6b09b27bf42b60b6e98bceafc63831e16a222a80172e7240bf";
                break;
            case AfdevBioBotCombatAction.FaceTarget:
                receiver = pawn;
                path = "Engine.Pawn.UpdatePawnRotation";
                flags = 0x20903;
                propertiesSize = parmsSize = 12;
                parameters = [new("NewRotation", "StructProperty", 12, 0, 0x80, "Core.Object.Rotator")];
                template = "1b00000000000000000000000000000000001e0000000016040b53";
                references = [(10, path + ".NewRotation")];
                names = [(1, "FaceRotation")];
                storageHash = "5804e96cc5da8398a2bb4f690286e9691ed892767fc3e8b0fc2bdf9dedcf211f";
                break;
            case AfdevBioBotCombatAction.SwitchToBestWeapon:
                path = "Engine.Controller.SwitchToBestWeapon";
                flags = 0x24202;
                propertiesSize = parmsSize = 4;
                parameters = [new("bForceNewWeapon", "BoolProperty", 4, 0, 0x90)];
                template = "0b07380084720100000000000000002a16182200721901000000000000000009000000000000000000000100000000000000002a1616040b1919010000000000000000090000000000000000000001000000000000000014000000000000000000001b00000000000000002d00000000000000000016040b53";
                references = [(7, "Engine.Controller.Pawn"), (23, "Engine.Controller.Pawn"),
                    (33, "Engine.Pawn.InvManager"), (43, "Engine.Pawn.InvManager"),
                    (59, "Engine.Controller.Pawn"), (69, "Engine.Pawn.InvManager"),
                    (79, "Engine.Pawn.InvManager"), (109, path + ".bForceNewWeapon")];
                names = [(99, "SwitchToBestWeapon")];
                storageHash = "378e4eea518a89f188b69a50c1e69baa3225764020d096f3861ce02b307c17c9";
                break;
            case AfdevBioBotCombatAction.LineOfSight:
                receiver = pawn;
                path = "Engine.Pawn.LineOfSightTo";
                flags = 0x20002;
                propertiesSize = parmsSize = 8;
                returnOffset = 4;
                parameters = [new("Other", "ObjectProperty", 4, 0, 0x80, "Engine.Actor"),
                    new("ReturnValue", "BoolProperty", 4, 4, 0x580)];
                template = "0482770100000000000000002a16182400190100000000000000000e0000000000000000000062020000000000000000004a4a1616043a000000000000000053";
                references = [(4, "Engine.Pawn.Controller"), (19, "Engine.Pawn.Controller"),
                    (29, "Engine.Controller.LineOfSightTo.ReturnValue"), (41, path + ".Other"), (55, path + ".ReturnValue")];
                storageHash = "0132528b030db8291f31d93de08d8f8f107b03b4742394970e7728977601af3e";
                break;
            case AfdevBioBotCombatAction.StartFire:
                path = "TGBioGame.TGBioPlayerController.StartFire";
                flags = 0x24202;
                propertiesSize = parmsSize = ByteParameterSize(path, pawn);
                parameters = [new("FireModeNum", "ByteProperty", 1, 0, 0x90)];
                template = "0b072300811b000000000000000016161c000000000000000000000000000000000016040b53";
                references = [(17, "UTGame.TGPlayerController.StartFire"), (26, path + ".FireModeNum")];
                names = [(6, "IsFiringDisabled")];
                storageHash = "920b2eb8e914c4bcd5caf2116c27bf2651cf32356b10e7c289ef0a0a72434bfb";
                break;
            case AfdevBioBotCombatAction.StopFire:
                // Matches super.StopFire in BIO IgnoreLookInput(true), allowing native fire-input clearing immediately after freezing.
                path = "UTGame.TGPlayerController.StopFire";
                flags = 0x24202;
                propertiesSize = parmsSize = ByteParameterSize(path, pawn);
                parameters = [new("FireModeNum", "ByteProperty", 1, 0, 0x90)];
                template = "0b0721009a383a000000000000000000383a240016142d010000000000000000281c000000000000000000000000000000000016040b53";
                references = [(8, path + ".FireModeNum"), (24, "UTGame.TGPlayerController.bLeftButtonPressed"),
                    (34, "Engine.PlayerController.StopFire"), (43, path + ".FireModeNum")];
                storageHash = "f832f020a62f14546b27287d54db59a13f112f8f9df947087c26a5d1b3e49f79";
                break;
            case AfdevBioBotCombatAction.Reload:
                path = "UTGame.TGPlayerController.SwitchClip";
                flags = 0x20202;
                propertiesSize = 4;
                parmsSize = 0;
                parameters = [new("TPawn", "ObjectProperty", 4, 0, 0, "UTGame.TGPawn")];
                template = "0f0000000000000000002e0000000000000000010000000000000000079000770000000000000000002a16e7a81f53776974636820436c697020616e642043757272656e742054696d65206973003855190100000000000000000900000000000000000000010000000000000000164a16190000000000000000000a000000000000000000001b000000000000000016040b53";
                references = [(2, path + ".TPawn"), (11, "UTGame.TGPawn"), (20, "Engine.Controller.Pawn"),
                    (33, path + ".TPawn"), (82, "Engine.Actor.WorldInfo"), (92, "Engine.WorldInfo.TimeSeconds"),
                    (102, "Engine.WorldInfo.TimeSeconds"), (115, path + ".TPawn")];
                names = [(135, "SwitchClip")];
                storageHash = "99956a254f178ecc8010ca70fdc09dbef5269dde5ddd71e8eef4d1168c0501c0";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        string declaringClass = string.Join('.', path.Split('.').Take(2));
        ResourceFunction function = catalog.Describe(receiver, reader.Path(reader.Identity(receiver).Class), path,
            flags, propertiesSize, parmsSize, returnOffset, (uint)template.Length / 2, storageHash, parameters,
            declaringClassPath: declaringClass);
        byte[] expected = Convert.FromHexString(template);
        byte[] actual = process.ReadMemory(function.ScriptPointer, expected.Length);
        var roots = new HashSet<uint> { function.Function.Address, receiver };

        if (function.ExecPointer != 0x0048DB10)
            throw new InvalidDataException("战斗入口不再使用已核脚本分派。");

        foreach ((int offset, string referencePath) in references)
        {
            uint address = Resolve(referencePath, pawn);

            roots.Add(address);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(offset, 4), address);
        }

        foreach ((int offset, string name) in names)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(offset, 4));

            if (reader.Name(index) != name || BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(offset + 4, 4)) != 0)
                throw new InvalidDataException("战斗脚本 FName 重定位与已核调用名称不符。");

            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(offset, 4), index);
        }

        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("战斗脚本完整重定位字节不符：" + path);

        var nativeProofs = new List<(uint Address, uint Value, uint Mask)>();

        if (action is AfdevBioBotCombatAction.Aim or AfdevBioBotCombatAction.FaceMovement or AfdevBioBotCombatAction.LineOfSight)
        {
            bool aiming = action != AfdevBioBotCombatAction.LineOfSight;
            uint nativeIndex = aiming ? 299u : 514u;
            uint exec = aiming ? 0x00934DA0u : 0x008EB380u;
            uint numericFunction = Resolve(aiming ? "Engine.Actor.SetRotation" : "Engine.Controller.LineOfSightTo", pawn);
            int bodyLength = aiming ? 0x70 : 0x143;
            string hash = aiming ? "e3924fc1cb23cd9db66ae5583f03c98184212f17dcb47b56041b004a52398f5c"
                : "3bb2d400fef2d658d1d921d6346ba586e992ed54ee6603b63c3a39856a2e335b";

            if (reader.U32(0x01FF4D10 + 4 * nativeIndex) != exec || reader.U16(numericFunction + 0xB0) != nativeIndex ||
                reader.U32(numericFunction + 0xC8) != exec || TickHook.Hash(process.ReadMemory(exec, bodyLength)) != hash)
                throw new InvalidDataException("瞄准或视线的数字 native 分派与当前镜像不符。");

            roots.Add(numericFunction);
            nativeProofs.Add((0x01FF4D10 + 4 * nativeIndex, exec, uint.MaxValue));
            nativeProofs.Add((numericFunction + 0xB0, nativeIndex, 0xFFFF));
            nativeProofs.Add((numericFunction + 0xC8, exec, uint.MaxValue));
        }

        return new CombatFunction(function, expected, roots.ToArray(), nativeProofs);
    }

    private ushort ByteParameterSize(string path, uint pawn)
    {
        uint function = Resolve(path, pawn);
        uint size = reader.U32(function + 0x50);
        ushort parms = reader.U16(function + 0xBE);

        // Accept only the live reflected one-byte parameter and trailing structure alignment. The property must remain at +0, size 1, with no other children.
        if (size is < 1 or > 4 || parms != size)
            throw new InvalidDataException("战斗 FireMode 参数区与单字节入口不符。");

        return parms;
    }

    private uint Resolve(string path, uint pawn)
    {
        string[] segments = path.Split('.');
        string classPath = string.Join('.', segments.Take(2));
        uint type = 0;
        var candidates = new List<uint> { controller, pawn, world.WorldInfo };

        if (classPath is "TGBioGame.TGBioPlayerReplicationInfo" or "Engine.PlayerReplicationInfo"
            or "Engine.TeamInfo" or "TGBioGame.TGBioMatch" or "TGBioGame.TGBioGameReplicationInfo")
        {
            uint pri = reader.U32(reader.FieldAddress(controller, "PlayerReplicationInfo",
                "Engine.Controller.PlayerReplicationInfo", "ObjectProperty", 4));
            uint team = reader.U32(reader.FieldAddress(pri, "Team", "Engine.PlayerReplicationInfo.Team", "ObjectProperty", 4));
            uint gri = reader.U32(reader.FieldAddress(world.WorldInfo, "GRI", "Engine.WorldInfo.GRI", "ObjectProperty", 4));

            candidates.AddRange([pri, team, world.GameInfo, gri]);
        }

        foreach (uint candidate in candidates)
        {
            if (candidate < 0x10000)
                continue;

            uint current = reader.Identity(candidate).Class;
            var seen = new HashSet<uint>();

            while (current != 0 && seen.Add(current) && seen.Count <= 32)
            {
                if (reader.Path(current) == classPath)
                {
                    type = current;
                    break;
                }

                current = reader.U32(current + 0x48);
            }

            if (type != 0)
                break;
        }

        if (type == 0)
            throw new InvalidDataException("战斗脚本引用不属于已核接收对象的类型链：" + path);

        for (int index = 2; index < segments.Length; index++)
            type = reader.FindField(type, segments[index], string.Join('.', segments.Take(index + 1)));

        return type;
    }
}

internal sealed record CombatFunction(ResourceFunction Function, byte[] Script, IReadOnlyList<uint> Roots,
    IReadOnlyList<(uint Address, uint Value, uint Mask)> NativeProofs);
