namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Deployment parameters for independent match processes, managed in appsettings.json without player state.
/// </summary>
public sealed class MatchServerOptions
{
    public string PythonExecutable { get; set; } = "";
    public int MaxInstances { get; set; } = 4;
    public int PublicPortBase { get; set; } = 65008;
    public int TargetPortBase { get; set; } = 7777;
    public double BridgeReadyTimeoutSeconds { get; set; } = 5;
    public double StartupTimeoutSeconds { get; set; } = 90;
    public bool BotFill { get; set; } = true;
    public uint[] BotModes { get; set; } = [0x204, 0x209, 0x1002];
    public int BotCountOverride { get; set; } = -1;
    public int BotSkillIndex { get; set; } = 6;
    public double BotDifficulty { get; set; } = 1;
    public int BotTeam { get; set; }
    public int BotRetries { get; set; } = 1;
    public double BotTopupIntervalSeconds { get; set; } = 5;
    public int BotMinReadyPlayers { get; set; } = 1;
    public bool BotRevive { get; set; }
    public bool BotAiAbilityCall { get; set; }

    internal void Validate()
    {
        if (MaxInstances is < 1 or > 256 || PublicPortBase < 1024 || TargetPortBase < 1024 ||
            (long)PublicPortBase + MaxInstances > 65536 || (long)TargetPortBase + MaxInstances > 65536)
            throw new InvalidDataException("Matches 对局实例数或端口范围无效。");
        if (BridgeReadyTimeoutSeconds <= 0 || StartupTimeoutSeconds < 5 ||
            !double.IsFinite(BridgeReadyTimeoutSeconds) || !double.IsFinite(StartupTimeoutSeconds))
            throw new InvalidDataException("Matches 启动超时时间无效。");
        if (BotModes is null || BotCountOverride < -1 || BotSkillIndex < 0 || BotRetries < 0 ||
            BotMinReadyPlayers < 0 || !double.IsFinite(BotDifficulty) ||
            !double.IsFinite(BotTopupIntervalSeconds) || BotTopupIntervalSeconds < 1)
            throw new InvalidDataException("Matches 机器人配置无效。");
    }
}
