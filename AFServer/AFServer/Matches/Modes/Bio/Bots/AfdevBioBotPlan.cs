using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Human slots and fill roster frozen for one start. Internal identities never enter authenticated
/// sessions, the lobby, or player saves.
/// </summary>
public sealed record AfdevBioBotPlan(Guid AllocationId, ulong RoomId, byte FighterCapacity,
    ImmutableArray<AfdevBioBotHumanIdentity> Humans, ImmutableArray<AfdevBioBotIdentity> Bots)
{
    /// <summary>Ordinary BIO inherits TGGame MaxPlayersAllowed=16 through TGRoundMatch/TGTeamGame.</summary>
    public const byte MaximumFighterCapacity = 16;

    /// <summary>
    /// Freezes all humans under the lobby transaction, including loading members and observers. Fills only
    /// vacant participant capacity.
    /// </summary>
    internal static AfdevBioBotPlan Capture(MatchStartSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);

        ImmutableArray<AfdevBioBotHumanIdentity> humans = [.. snapshot.Members.Select(member =>
            new AfdevBioBotHumanIdentity(member.Session.SessionId, member.Profile.Uin, member.Profile.Nickname, member.Observer))];
        int botCount = snapshot.FighterCapacity - humans.Count(human => !human.Observer);
        var bots = ImmutableArray.CreateBuilder<AfdevBioBotIdentity>(botCount);
        var botUins = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < botCount; index++)
        {
            Guid botId;
            string botUin;

            // Nonnumeric identities consume no human UIN. Native preparation still checks complete identities and live PRI conflicts individually.
            do
            {
                botId = Guid.NewGuid();
                botUin = "bot-" + botId.ToString("N")[..12];
            }
            while (!botUins.Add(botUin));

            bots.Add(new(botId, botUin, $"BIO Bot {index + 1}"));
        }

        return new(snapshot.AllocationId, snapshot.RoomId, snapshot.FighterCapacity, humans, bots.MoveToImmutable());
    }

    /// <summary>
    /// Checks that the plan still belongs to the same frozen snapshot. Never infer human slots from native
    /// counts or loaded connections.
    /// </summary>
    internal void Validate(MatchStartSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        Validate();

        if (AllocationId != snapshot.AllocationId || RoomId != snapshot.RoomId ||
            FighterCapacity != snapshot.FighterCapacity ||
            !Humans.SequenceEqual(snapshot.Members.Select(member => new AfdevBioBotHumanIdentity(
                member.Session.SessionId, member.Profile.Uin, member.Profile.Nickname, member.Observer))))
            throw new InvalidOperationException("Bot 计划与当前冻结开赛快照的容量或精确真人身份不一致。");
    }

    /// <summary>
    /// Direct diagnostic-plan binding preserves full roster boundaries too. The plan itself proves no
    /// native registration or spawn.
    /// </summary>
    internal void Validate()
    {
        ValidateFighterCapacity(FighterCapacity);

        if (AllocationId == Guid.Empty || RoomId == 0 || Humans.IsDefaultOrEmpty || Bots.IsDefault ||
            Humans.Any(human => human.SessionId == Guid.Empty || human.Uin == 0 || string.IsNullOrWhiteSpace(human.Nickname)) ||
            Humans.Select(human => human.SessionId).Distinct().Count() != Humans.Length ||
            Humans.Select(human => human.Uin).Distinct().Count() != Humans.Length)
            throw new InvalidOperationException("Bot 计划必须保留互不重复的精确真人 Session 与 UIN。");

        int humanCount = Humans.Count(human => !human.Observer);

        if (humanCount < 1 || humanCount > FighterCapacity || Bots.Length != FighterCapacity - humanCount ||
            Bots.Any(bot => bot.BotId == Guid.Empty || bot.Uin != "bot-" + bot.BotId.ToString("N")[..12] ||
                string.IsNullOrWhiteSpace(bot.Nickname)) ||
            Bots.Select(bot => bot.BotId).Distinct().Count() != Bots.Length ||
            Bots.Select(bot => bot.Uin).Distinct(StringComparer.Ordinal).Count() != Bots.Length)
            throw new InvalidOperationException("Bot 名单必须按冻结参战容量减真人名额生成，且每个 Bot 身份互不重复。");
    }

    /// <summary>
    /// Even with bots disabled, validates the same start capacity and human snapshot. Never silently
    /// truncate invalid counts before native OPEN.
    /// </summary>
    internal static void ValidateSnapshot(MatchStartSnapshot snapshot)
    {
        AfdevMatchStartValidator.Validate(snapshot);

        if (!AfdevMapCatalog.SupportsBioBots(snapshot.Descriptor.ModeId, snapshot.Descriptor.MapId))
            throw new InvalidOperationException("Bot 计划及原生容量仅接受已接入的普通 BIO 地图与真实开赛快照。");
    }

    internal static void ValidateFighterCapacity(int capacity)
    {
        if (capacity is < 1 or > MaximumFighterCapacity)
            throw new InvalidOperationException($"普通 BIO 参战容量必须为 1–{MaximumFighterCapacity}，实际为 {capacity}。");
    }
}

/// <summary>
/// Exact frozen human identity. Observers consume no participant slot; native PC/PRI still requires normal
/// admission validation.
/// </summary>
public sealed record AfdevBioBotHumanIdentity(Guid SessionId, ulong Uin, string Nickname, bool Observer = false);

/// <summary>
/// Bot identity belonging only to this allocation. Creates no human authentication session or MongoDB
/// player record.
/// </summary>
public sealed record AfdevBioBotIdentity(Guid BotId, string Uin, string Nickname);
