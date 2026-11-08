namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Describes internal bot-identity initialization only. Calls still require the exact world's game-thread
/// transaction.
/// </summary>
sealed class AfdevBioBotIdentityFunctions(AfdevOwnedProcess process)
{
    private readonly ResourceFunctionCatalog functions = new(process);

    public const uint NextPlayerIdExec = 0x00943730;

    /// <summary>
    /// Process-level ID sequence in the current PE. Native code returns the old value and increments it,
    /// without reading GameInfo.CurrentID.
    /// </summary>
    public const uint NextPlayerIdSequenceAddress = 0x01FA8E50;

    /// <summary>
    /// Allocates one engine PlayerID. Zero is valid; nonzero alone does not prove successful execution.
    /// </summary>
    public ResourceFunction DescribeNextPlayerId(uint game)
    {
        // The exact entry jumps to the postincrement-only body. Calls no Login/PostLogin and changes no participant counts.
        Verify(0x00BE5610, "8B0D508EFA018BC141890D508EFA01C3");
        Verify(0x01FA37A8, "B404B20130379400");
        Verify(NextPlayerIdExec, "8B442404FF4018568BF18B48188039417510416A008948188B481450FF15144EFF018BCEE8B71E2A008B4C240C89015EC20800");

        ResourceFunction function = functions.Describe(game, "TGBioGame.TGBioMatch", "Engine.GameInfo.GetNextPlayerID",
            0x20401, 4, 4, 0, 2,
            "ccaf083429bf5a89fa542d244998f2bbd2f8837281164524d82f612695ccb1db",
            [new("ReturnValue", "IntProperty", 4, 0, 0x580)]);

        if (function.ExecPointer != NextPlayerIdExec)
            throw new InvalidDataException("GetNextPlayerID 的原生入口与当前 PE 证据不符。");

        return function;
    }

    /// <summary>
    /// Uses PRI's original setter to create an owned FString and maintain OldName and replication-update
    /// flags.
    /// </summary>
    public ResourceFunction DescribeSetPlayerName(uint pri)
    {
        ResourceFunction function = functions.Describe(pri, "TGBioGame.TGBioPlayerReplicationInfo",
            "Engine.PlayerReplicationInfo.SetPlayerName", 0x20802, 12, 12, 0xFFFF, 175,
            "c167427cf77be3d2dcdc76d6832bb2c6876014c5dd58b8b88c34980f17393ccf",
            [new("S", "StrProperty", 12, 0, 0x400080)]);

        if (function.ExecPointer != 0x0048DB10)
            throw new InvalidDataException("SetPlayerName 不再使用已核实的脚本执行入口。");

        return function;
    }

    private void Verify(uint address, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);

        if (!process.ReadMemory(address, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Bot 身份入口的当前 PE 校验失败。");
    }
}
