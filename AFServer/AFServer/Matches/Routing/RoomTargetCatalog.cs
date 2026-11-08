namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Exact client-map/GameInfo routing table. A record's existence does not imply backend support for the
/// mode.
/// </summary>
public static class RoomTargetCatalog
{
    /// <summary>Exact route version bound to the release manifest. Update alongside routing contents.</summary>
    public const string Version = "ph-1.0.0.24-rooms-v3";

    private static readonly Dictionary<(uint Mode, ushort Map), RoomTarget> Targets = new()
    {
        [(0x2001, 0x002F)] = new("SV-Maya_3_Main", "PVEGame.TGSVGame"),
        [(0x2001, 0x0007)] = new("SV-Factory_1_Main", "PVEGame.TGSVGame"),
        [(0x2002, 0x0010)] = new("IF-Factory_3_Main", "PVEGame.TGIFGame"),
        [(0x2002, 0x005F)] = new("IF2-Factory_83_main", "TGIFGame.TGIF2Game"),
        [(0x2003, 0x003F)] = new("LD-Tutorial_5_Main", "LDGame.TGLDGame"),
        [(0x2005, 0x0060)] = new("ATD-Capital_17_Main", "ATDGame.ATDGameInfo"),
        [(0x2005, 0x008D)] = new("ATD-Capital_22_B_Main", "ATDGame.ATDGameInfo"),
        [(0x0103, 0x0080)] = new("SV-Maya_3_Main", "PVEGame.TGSVGame"),
        [(0x0204, 0x007A)] = new("Bio-Maya_7_Main", "TGBioGame.TGBioMatch"),
        [(0x0204, 0x0076)] = new("Bio-Capital_12_Main", "TGBioGame.TGBioMatch"),
        // 2014 settings: MapId=74 (Mutation Zone), ModeId=516; BIO prefixes map to TGBioMatch. Source is referenced in round9-mutation-target.md.
        [(0x0204, 0x004A)] = new("Bio-Factory_22_Main", "TGBioGame.TGBioMatch"),
        [(0x0204, 0x0027)] = new("Bio-Capital_4_Main", "TGBioGame.TGBioMatch"),
        [(0x0204, 0x003C)] = new("Bio-Maya_6_Main", "TGBioGame.TGBioMatch"),
        [(0x0209, 0x0090)] = new("Bio2-Maya_7_Main", "TGBio2Game.TGBio2Match"),
        [(0x0209, 0x0048)] = new("Bio2-Capital_12_Main", "TGBio2Game.TGBio2Match"),
        [(0x0209, 0x0074)] = new("Bio2-Factory_22_Main", "TGBio2Game.TGBio2Match"),
        [(0x0209, 0x0073)] = new("Bio2-Capital_4_Main", "TGBio2Game.TGBio2Match"),
        [(0x0209, 0x0075)] = new("Bio2-Maya_6_Main", "TGBio2Game.TGBio2Match"),
        [(0x0201, 0x0051)] = new("MHM-Capital_14_Main", "PVEGame.TGMechaHumanMatch"),
        [(0x0201, 0x0024)] = new("MHM-Capital3_Main", "PVEGame.TGMechaHumanMatch"),
        [(0x0201, 0x0031)] = new("MHM-Factory_20_Main", "PVEGame.TGMechaHumanMatch"),
        [(0x0201, 0x0037)] = new("MHM-Factory_21_Main", "PVEGame.TGMechaHumanMatch"),
        [(0x0202, 0x0036)] = new("MTM-Factory_100_Main", "MechaGame.TGMechaTeamMatch"),
        [(0x0203, 0x0022)] = new("TTM-Tank_01_Main", "TGTankGame.TGTankTeamMatch"),
        [(0x0203, 0x0069)] = new("TTM-Tank_03_Main", "TGTankGame.TGTankTeamMatch"),
        [(0x0205, 0x0023)] = new("CTM-Seashore_6_Main", "PVEGame.TGCrazyTeamMatch"),
        [(0x0206, 0x002C)] = new("TDB-Tank_02_Main", "TGTankGame.TGTankDBMatch"),
        [(0x0207, 0x003D)] = new("MDM-Factory_101_Main", "MechaGame.TGMechaDoomMatch"),
        [(0x020A, 0x003D)] = new("MDM-Factory_101_Main", "MechaGame.TGMechaDoomMatch"),
        [(0x020A, 0x0036)] = new("MTM-Factory_100_Main", "MechaGame.TGMechaTeamMatch"),
        // Old GameMapSettings omit prefixes or incorrectly target Navart. Use the actual main world in the reserved runtime package.
        [(0x1001, 0x0002)] = new("BM-Seashore_1_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0008)] = new("BM-Capital_1_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0009)] = new("BM-Factory_13_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0016)] = new("BM-Factory_7_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0039)] = new("BM-Capital_9_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x002D)] = new("BM-Capital_5_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x003A)] = new("BM-nuke_main", "UTGame.TGBombMatch"),
        [(0x1001, 0x003B)] = new("BM-Maya_4_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x003E)] = new("BM-Tutorial_4_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0040)] = new("BM-Tuscan_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0045)] = new("BM-Seashore_9_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0050)] = new("BM-Factory_26_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0054)] = new("BM-Capital_15_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0056)] = new("BM-Forge_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x006C)] = new("BM-Factory_102_Main", "UTGame.TGBombMatch"),
        [(0x1001, 0x0086)] = new("BM-Factory_37_Main", "UTGame.TGBombMatch"),
        // DefaultEngine.TGMatchTypeMappings(4098) includes three old maps too. Actual packages have TM- prefixes omitted by old GameMapSettings.
        [(0x1002, 0x0003)] = new("TM-Ship_4_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x000E)] = new("TM-Maya_1_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x0013)] = new("TM-Ship_5_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x002A)] = new("TM-Maya_2_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x002B)] = new("TM-Capital_6_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x0041)] = new("TM-Seashore_8_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x0052)] = new("TM-Ship_6_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x006B)] = new("TM-Factory_28_Main", "UTGame.TGTeamMatch"),
        [(0x1002, 0x0088)] = new("AM-Factory_35_simple_Main", "UTGame.TGTeamMatch"),
        [(0x1003, 0x0043)] = new("AM-Factory_8_Main", "UTGame.TGAnlMatch"),
        [(0x1003, 0x006E)] = new("TM-Factory_28_Main", "UTGame.TGAnlMatch"),
        [(0x1003, 0x0089)] = new("AM-Factory_35_simple_Main", "UTGame.TGAnlMatch"),
        [(0x1004, 0x0011)] = new("BM-Seashore_1_Main", "UTGame.TGIndMatch"),
        [(0x1004, 0x0020)] = new("IM-Factory18_Main", "UTGame.TGIndMatch"),
        [(0x1004, 0x0047)] = new("BM-Capital_1_Main", "UTGame.TGIndMatch"),
        [(0x1004, 0x0049)] = new("BM-Factory_13_Main", "UTGame.TGIndMatch"),
        [(0x1004, 0x004B)] = new("BM-Capital_5_Main", "UTGame.TGIndMatch"),
        [(0x1007, 0x005A)] = new("TP-Factory_27_Main", "UTGame.TGEscapeMatch"),
        [(0x1008, 0x005E)] = new("TM-Ship7_Main", "UTGame.TGSuperTeamMatch"),
        [(0x5001, 0x001E)] = new("TM-Knife_3_Main", "UTGame.TGTeamMatch"),
        [(0x5001, 0x0028)] = new("IM-Knife_4_Main", "UTGame.TGTeamMatch"),
        [(0x5001, 0x0032)] = new("TM-Knife_5_Main", "UTGame.TGTeamMatch"),
        [(0x5001, 0x005C)] = new("TM-Knife_6_Main", "UTGame.TGTeamMatch"),
        [(0x5001, 0x0072)] = new("TM-Knife_7_Main", "UTGame.TGTeamMatch"),
        [(0x5002, 0x0038)] = new("TM-Maya_5_main", "UTGame.TGTeamMatch"),
        [(0x5005, 0x0026)] = new("TM-Grenade_1_Main", "UTGame.TGTeamMatch"),
        [(0x5005, 0x0058)] = new("TM-Grenade_2_Main", "UTGame.TGTeamMatch"),
        [(0x9001, 0x001C)] = new("TM-Tutorial_1_Main", "UTGame.TGTeamMatch"),
        [(0x9001, 0x005D)] = new("TM-Tutorial_9_Main", "UTGame.TGTeamMatch"),
    };

    /// <summary>
    /// Resolves registered mode/map pairs, rejecting unknown combinations so room maps cannot differ from
    /// loaded maps.
    /// </summary>
    /// <exception cref="InvalidOperationException">The exact mode/map pair is unregistered.</exception>
    public static RoomTarget Resolve(uint mode, ushort map)
    {
        if (Targets.TryGetValue((mode, map), out var target)) return target;

        throw new InvalidOperationException($"未登记的对局组合，模式：0x{mode:X8}，地图：0x{map:X4}。");
    }
}

public sealed record RoomTarget(string MapName, string GameClass);
