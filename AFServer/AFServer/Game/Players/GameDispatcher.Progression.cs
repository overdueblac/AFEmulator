namespace AFServer.AssaultFire.Game;

// Progression, achievement, and calling-card requests remain serialized by dispatchGate; client UINs are used only for public-profile queries.
public sealed partial class GameDispatcher
{
    private async ValueTask<bool> HandleProgressionAsync(GameSession session, PlayerState player, ushort command,
        byte[] body, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case 0xC001:
                await SendAchievementDetailsAsync(session, player, body, cancellationToken);
                return true;
            case 0xC003:
                var listReader = new TdrReader(body);

                listReader.U8();
                listReader.End();
                await SendAsync(session, 0xC004, ProfilePackets.AchievementList(player), cancellationToken);
                return true;
            case 0xC006:
                await ModifyNameCardAsync(session, player, body, cancellationToken);
                return true;
            case 0xC008:
                var statisticsReader = new TdrReader(body);
                var statisticsUin = statisticsReader.U64();

                statisticsReader.End();
                statisticsUin = statisticsUin == 0 ? session.Uin : statisticsUin;

                var statisticsPlayer = await LoadPublicPlayerAsync(player, statisticsUin, cancellationToken);

                await SendAsync(session, 0xC009, ProfilePackets.AchievementStatistics(statisticsUin, statisticsPlayer), cancellationToken);
                return true;
            case 0xC00A:
                var historyReader = new TdrReader(body);
                var historyUin = historyReader.U64();
                var achievementId = historyReader.U32();

                historyReader.End();
                historyUin = historyUin == 0 ? session.Uin : historyUin;

                var historyPlayer = await LoadPublicPlayerAsync(player, historyUin, cancellationToken);

                await SendAsync(session, 0xC00B, ProfilePackets.AchievementHistory(historyUin, achievementId, historyPlayer), cancellationToken);
                return true;
            case 0xC00C:
                var checkReader = new TdrReader(body);

                checkReader.U32();
                checkReader.U64();
                checkReader.End();
                // The native trigger check is one-way. All achievements are already completed by default, so do not repeat rewards or create unlock popups.
                return true;
            case 0xC00D:
                var cardReader = new TdrReader(body);
                var cardUin = cardReader.U64();

                cardReader.End();
                cardUin = cardUin == 0 ? session.Uin : cardUin;

                var cardPlayer = await LoadPublicPlayerAsync(player, cardUin, cancellationToken);

                await SendAsync(session, 0xC00E, ProfilePackets.NameCard(cardUin, cardPlayer), cancellationToken);
                return true;
            case 0xC00F:
                var noticeReader = new TdrReader(body);
                var noticeId = unchecked((int)noticeReader.U32());

                noticeReader.End();

                var notice = ProfilePackets.NameCard(session.Uin, player, noticeId);

                await SendAsync(session, 0xC010, notice, cancellationToken);
                if (session.RoomId is { } roomId && rooms.TryGetValue(roomId, out var room))
                    await BroadcastAsync(room, 0xC010, notice, cancellationToken, session);
                return true;
            case 0xC011:
                await ClaimAchievementAwardAsync(session, player, body, cancellationToken);
                return true;
            case 0xA325:
                var privacyReader = new TdrReader(body);
                var privacyFlags = privacyReader.U16();

                privacyReader.End();
                if (player.Progression.PrivacyFlags != privacyFlags)
                {
                    player.Progression.PrivacyFlags = privacyFlags;
                    await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
                }
                return true;
            default:
                return false;
        }
    }

    private async ValueTask SendAchievementDetailsAsync(GameSession session, PlayerState player, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var uin = reader.U64();
        var group = reader.U8();

        reader.End();
        uin = uin == 0 ? session.Uin : uin;

        var requestedPlayer = group < ProgressionCatalog.GroupCount ? await LoadPublicPlayerAsync(player, uin, cancellationToken) : null;
        var definitions = requestedPlayer is null ? [] : ProgressionCatalog.Instance.InGroup(group).ToArray();

        if (requestedPlayer is null || definitions.Length == 0)
        {
            await SendAsync(session, 0xC002, ProfilePackets.AchievementDetails(uin, group, requestedPlayer, [],
                requestedPlayer is null ? GameWire.AchievementWrongData : GameWire.AchievementSuccess), cancellationToken);
            return;
        }
        // Native code updates the achievement cache entry by entry. Split categories across packets, keeping every packet within TDR's 100-entry limit.
        foreach (var chunk in definitions.Chunk(100))
            await SendAsync(session, 0xC002, ProfilePackets.AchievementDetails(uin, group, requestedPlayer, chunk), cancellationToken);
    }

    private async ValueTask<PlayerState?> LoadPublicPlayerAsync(PlayerState current, ulong uin, CancellationToken cancellationToken)
    {
        if (uin == current.Uin) return current;

        var document = await repository.LoadPlayerAsync(uin, cancellationToken);

        if (document is null) return null;

        var player = new PlayerState(uin, document);

        if (ProgressionUnlocks.Ensure(player, (timeProvider ?? TimeProvider.System).GetUtcNow()))
            await repository.SavePlayerAsync(uin, player.Document, cancellationToken);
        return player;
    }

    private async ValueTask ModifyNameCardAsync(GameSession session, PlayerState player, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var cardId = reader.U32();
        var achievementId = reader.U32();
        var level = reader.U8();
        var catalog = ProgressionCatalog.Instance;

        reader.End();
        cardId = cardId == 0 ? ProgressionCatalog.DefaultCardId : cardId;

        var valid = cardId == ProgressionCatalog.DefaultCardId ||
            catalog.Cards.TryGetValue(cardId, out var tier) && tier.Id == achievementId &&
            // The native calling-card panel converts single-level achievement Level=0 to request Level=1. Still accept only the card belonging to that achievement.
            (tier.IsLeveled ? tier.Level == level : level is 0 or 1) && player.Progression.OwnsCard(tier);

        if (valid && player.Progression.CardId != cardId)
        {
            player.Progression.CardId = cardId;
            await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
        }
        await SendAsync(session, 0xC007, new TdrWriter().U32(valid ? GameWire.AchievementSuccess : GameWire.AchievementWrongData)
            .U32(valid ? cardId : player.Progression.CardId), cancellationToken);
    }

    private async ValueTask ClaimAchievementAwardAsync(GameSession session, PlayerState player, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var level = reader.U16();
        var itemId = reader.U32();

        reader.End();
        if (!ProgressionCatalog.Instance.Awards.TryGetValue(level, out var award) ||
            player.Progression.AchievementScore < award.RequiredScore || (itemId != 0 && !award.Items.Any(item => item.ItemId == itemId)))
        {
            await SendAsync(session, 0xC012, new TdrWriter().U32(GameWire.AchievementWrongData).U16(level), cancellationToken);
            return;
        }

        var flag = 1U << (level - 1);
        var changedInventory = false;

        if ((player.Progression.AwardFlags & flag) == 0)
        {
            var owned = player.Props.Select(prop => PlayerState.Number(prop, "item_id")).ToHashSet();

            foreach (var item in award.Items)
            {
                // Do not issue unlocked items again. New rewards follow the server-wide permanence policy and preserve equipment relationships.
                if (!owned.Add(item.ItemId)) continue;

                var prop = PlayerState.NewProp(player.AllocateGid(), item.ItemId);

                player.Inventory.Add((System.Text.Json.Nodes.JsonNode)prop);
                changedInventory = true;
            }
            player.Progression.AwardFlags |= flag;
            // Delivery and claim markers share one CAS commit. Never send success before a potentially failing database operation.
            await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
        }
        await SendAsync(session, 0xC012, new TdrWriter().U32(GameWire.AchievementSuccess).U16(level), cancellationToken);
        if (changedInventory) await SendInventoryAsync(session, player, cancellationToken);
    }
}
