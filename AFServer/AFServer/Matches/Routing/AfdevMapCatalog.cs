namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Exact integrated AFDEV mode/map scope. Static registration never replaces per-start native-world checks.
/// </summary>
public static class AfdevMapCatalog
{
    /// <summary>Exact map ranges for ordinary BIO, PVE, team matches, Bomb, and free-for-all.</summary>
    public static bool IsSupported(uint modeId, ushort mapId)
        => SupportsBioBots(modeId, mapId) || SupportsTowerDefense(modeId, mapId) ||
            SupportsDefense(modeId, mapId) || SupportsSurvival(modeId, mapId) || SupportsEncirclement(modeId, mapId) ||
            SupportsPvp(modeId, mapId);

    /// <summary>
    /// Integrated ordinary PvP only. Exact map lists do not authorize ranked or other special modes.
    /// </summary>
    public static bool SupportsPvp(uint modeId, ushort mapId)
        => SupportsTeamDeathmatch(modeId, mapId) || SupportsBomb(modeId, mapId) || SupportsIndividual(modeId, mapId);

    /// <summary>Bomb mode enables all sixteen maps in this build's lobby.</summary>
    public static bool SupportsBomb(uint modeId, ushort mapId)
        => modeId == 0x1001 && mapId is 0x0002 or 0x0008 or 0x0009 or 0x0016 or 0x002D or 0x0039 or
            0x003A or 0x003B or 0x003E or 0x0040 or 0x0045 or 0x0050 or 0x0054 or 0x0056 or 0x006C or 0x0086;

    /// <summary>Free-for-all enables all five maps in this build's lobby.</summary>
    public static bool SupportsIndividual(uint modeId, ushort mapId)
        => modeId == 0x1004 && mapId is 0x0011 or 0x0020 or 0x0047 or 0x0049 or 0x004B;

    /// <summary>Bomb/team modes share fixed team-seat protocol; free-for-all uses consecutive seats.</summary>
    internal static bool UsesTeamSeats(uint modeId) => modeId is 0x1001 or 0x1002;

    /// <summary>
    /// Ordinary team mode uses TGTeamMatch. Knife, sniper, and other PvP modes require separate
    /// integration, even if they share a rule class.
    /// </summary>
    public static bool SupportsTeamDeathmatch(uint modeId, ushort mapId)
        => modeId == 0x1002 && mapId is 0x0003 or 0x000E or 0x0013 or 0x002A or 0x002B or 0x0041 or 0x0052 or 0x006B or 0x0088;

    /// <summary>
    /// Encirclement enables Hideout only. LD rules drive native enemies/levels, without player-bot support.
    /// </summary>
    public static bool SupportsEncirclement(uint modeId, ushort mapId)
        => modeId == 0x2003 && mapId == 0x003F;

    /// <summary>
    /// Survival enables Altar and Assembly Center. TGSV rules drive native monsters, without player-bot
    /// support.
    /// </summary>
    public static bool SupportsSurvival(uint modeId, ushort mapId)
        => modeId == 0x2001 && mapId is 0x002F or 0x0007;

    /// <summary>
    /// Integrated Tower Defense maps share native ATD rules, difficulty, and settlement, without player-bot
    /// support.
    /// </summary>
    public static bool SupportsTowerDefense(uint modeId, ushort mapId)
        => modeId == 0x2005 && mapId is 0x0060 or 0x008D;

    /// <summary>
    /// Defense enables Iron Forest and Starship Invasion, checking IF/IF2 native rules and statistic
    /// layouts separately.
    /// </summary>
    public static bool SupportsDefense(uint modeId, ushort mapId)
        => modeId == 0x2002 && mapId is 0x0010 or 0x005F;

    /// <summary>
    /// Exact scope of custom ordinary-BIO bots. Native team bots and PVE enemies are managed separately.
    /// </summary>
    public static bool SupportsBioBots(uint modeId, ushort mapId)
        => modeId == 0x0204 && mapId is 0x004A or 0x007A or 0x0027 or 0x0076 or 0x003C;

    /// <summary>
    /// Gets the exact map package/GameInfo for integrated combinations, rejecting all others.
    /// </summary>
    public static RoomTarget Resolve(uint modeId, ushort mapId)
    {
        if (!IsSupported(modeId, mapId))
            throw new InvalidOperationException($"AFDEV 未接入此模式与地图：mode=0x{modeId:X4} map=0x{mapId:X4}。");

        return RoomTargetCatalog.Resolve(modeId, mapId);
    }

    /// <summary>
    /// Shared Tower Defense boundary for difficulty writes and settlement reads. Map ID, actual package,
    /// and GameInfo must all match.
    /// </summary>
    internal static bool MatchesTowerDefense(AfdevNativeEvidence evidence)
        => SupportsTowerDefense(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>
    /// Defense difficulty/settlement validate map ID, package, and rule class together, never mixing IF and
    /// IF2 worlds.
    /// </summary>
    internal static bool MatchesDefense(AfdevNativeEvidence evidence)
        => SupportsDefense(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>
    /// Survival difficulty/settlement require integrated map ID, package, and TGSVGame to match together.
    /// </summary>
    internal static bool MatchesSurvival(AfdevNativeEvidence evidence)
        => SupportsSurvival(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>
    /// Encirclement difficulty/settlement validate its unique map ID, package, and TGLDGame together.
    /// </summary>
    internal static bool MatchesEncirclement(AfdevNativeEvidence evidence)
        => SupportsEncirclement(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>Shared settings boundary for integrated PVE, without granting player-bot support.</summary>
    internal static bool MatchesPve(AfdevNativeEvidence evidence)
        => MatchesTowerDefense(evidence) || MatchesDefense(evidence) || MatchesSurvival(evidence) ||
            MatchesEncirclement(evidence);

    /// <summary>
    /// Validates BIO-specific evidence. Enabling other modes must not expand bot, navigation, or
    /// demonstration-import scope.
    /// </summary>
    internal static bool MatchesBio(AfdevNativeEvidence evidence)
        => SupportsBioBots(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>
    /// Team settings, bots, and settlement require both this round's map package and TGTeamMatch evidence
    /// to match.
    /// </summary>
    internal static bool MatchesTeamDeathmatch(AfdevNativeEvidence evidence)
        => SupportsTeamDeathmatch(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    /// <summary>
    /// Shared PvP operations require the exact map package and mode-specific GameInfo together.
    /// </summary>
    internal static bool MatchesPvp(AfdevNativeEvidence evidence)
        => SupportsPvp(evidence.ModeId, evidence.MapId) && MatchesRoute(evidence);

    private static bool MatchesRoute(AfdevNativeEvidence evidence)
    {
        RoomTarget target = Resolve(evidence.ModeId, evidence.MapId);

        return evidence.MapName == target.MapName && evidence.GameClass == target.GameClass;
    }
}
