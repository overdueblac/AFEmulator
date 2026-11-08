namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevMatchAllocator
{
    private readonly bool bioBotsEnabled = validation.Options.BioBots.Enabled;

    /// <summary>
    /// Freezes the bot-fill plan after the lobby start snapshot and before issuing grants. Uses
    /// configuration captured at service construction only.
    /// </summary>
    public MatchStartSnapshot FreezeBotPlan(MatchStartSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.BioBotPlan is not null)
            throw new InvalidOperationException("开赛 Bot 计划已经冻结，不能重新分配内部身份。");

        AfdevMatchStartValidator.Validate(snapshot);

        return RequiresBioBotPlan(snapshot) ? snapshot with { BioBotPlan = AfdevBioBotPlan.Capture(snapshot) } : snapshot;
    }

    private void ValidateBotPlan(MatchStartSnapshot snapshot)
    {
        AfdevMatchStartValidator.Validate(snapshot);

        if (RequiresBioBotPlan(snapshot) != (snapshot.BioBotPlan is not null))
            throw new InvalidOperationException("开赛 Bot 计划与本局玩法及服务配置不一致；PVE 不接受玩家 Bot 名册。");

        snapshot.BioBotPlan?.Validate(snapshot);
    }

    private bool RequiresBioBotPlan(MatchStartSnapshot snapshot)
        => bioBotsEnabled && AfdevMapCatalog.SupportsBioBots(snapshot.Descriptor.ModeId, snapshot.Descriptor.MapId);
}
