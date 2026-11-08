using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// GEO/ZONE application services. Identity comes from the authenticated connection; request UINs do not
/// determine authorization.
/// </summary>
public sealed partial class GameDispatcher(
    IGameRepository repository,
    IMatchServerAllocator allocator,
    string publicAddress = "127.0.0.1",
    ushort zonePort = 65006,
    Action<string>? protocolLog = null,
    TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly Dictionary<ulong, GameSession> online = [];
    private readonly Dictionary<ulong, GameRoom> rooms = [];
    private readonly HashSet<GameSession> pendingProfiles = [];
    private readonly Dictionary<GameSession, int> lobbyMarkers = [];
    private JsonObject? catalog;
    private ulong nextRoomId = 0x0000271100000001;

    /// <summary>
    /// Serializes business-state commits before success responses. Mutated player documents are not reused
    /// after exceptions.
    /// </summary>
    public async ValueTask HandleAsync(GameSession session, byte[] plaintext, CancellationToken cancellationToken)
    {
        var receivedAt = Stopwatch.GetTimestamp();
        var (magic, command, body) = GameWire.Parse(plaintext);
        protocolLog?.Invoke($"RX UIN={session.Uin} {(magic == GameWire.GeoMagic ? "GEO" : "ZONE")} 命令=0x{command:X4} 名称={GameWire.Describe(magic, command)} body={body.Length}B");
        await dispatchGate.WaitAsync(cancellationToken);
        try
        {
            if (session.IsDisconnected) throw new InvalidOperationException("已断开的游戏会话不能重新使用。");
            if (magic == GameWire.GeoMagic)
            {
                await HandleGeoAsync(session, command, body, cancellationToken);
                return;
            }
            if (magic != GameWire.ZoneMagic) throw new InvalidDataException($"未知应用魔数：0x{magic:X4}。");
            await RetryAbandonedMatchReleasesAsync();
            online[session.Uin] = session;

            var document = await repository.LoadPlayerAsync(session.Uin, cancellationToken);
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var player = document is null ? PlayerState.Create(session.Uin, now) : new PlayerState(session.Uin, document);

            session.Nickname = player.Nickname;
            catalog ??= await repository.LoadCatalogAsync(cancellationToken);

            // Idempotently completes skills, items, and progression for new players, existing saves, and explicit imports, committing migration once per player.
            var skillsChanged = SkillUnlocks.Ensure(player);
            var inventoryChanged = InventoryUnlocks.Ensure(player, catalog);
            var backpacksChanged = (document is null || command is 0xA000 or 0xFF05)
                && BackpackLoadouts.Ensure(player, catalog);
            var equipmentChanged = (document is null || command is 0xA000 or 0xFF05)
                && FunctionalEquipment.Ensure(player);
            var progressionChanged = ProgressionUnlocks.Ensure(player, now);

            if (document is null || skillsChanged || inventoryChanged || backpacksChanged || equipmentChanged || progressionChanged)
                await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
            switch (command)
            {
                case 0xA000:
                    if (body.Length != 12) throw new InvalidDataException("登录请求必须为 12 字节。");
                    pendingProfiles.Add(session);
                    await SendAsync(session, 0xA001, new TdrWriter().U16(GameWire.ZoneSuccess).U32(1).U32(1)
                        .Double(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).U32(player.Balance("ap")).U32(player.Balance("gp")).U16(0), cancellationToken);
                    break;
                case 0xFF05:
                    if (!pendingProfiles.Remove(session)) break;
                    // FF05 marks the point when the client can receive profile data; do not push it immediately after the login ACK.
                    await session.SendAsync(ProfilePackets.PlayerInfo(player, 0), cancellationToken);
                    foreach (var packet in ProfilePackets.Inventory(player, login: true)) await session.SendAsync(packet, cancellationToken);
                    await session.SendAsync(ProfilePackets.PlayerInfo(player, player.CurrentRole), cancellationToken);
                    await SendWalletAsync(session, player, cancellationToken);
                    await SendAsync(session, 0xFF13, new TdrWriter().U16(0), cancellationToken);
                    break;
                case 0xA004:
                    await SendAsync(session, 0xFF10, new TdrWriter().U8(0), cancellationToken);
                    break;
                case 0xA002:
                    var nicknameLength = Array.IndexOf(body, (byte)0);

                    if (nicknameLength is < 1 or > 31) throw new InvalidDataException("创建角色昵称长度无效。");
                    player.Nickname = Encoding.UTF8.GetString(body.AsSpan(0, nicknameLength));
                    await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
                    session.Nickname = player.Nickname;
                    await SendAsync(session, 0xA003, new TdrWriter().U16(GameWire.ZoneSuccess), cancellationToken);
                    break;
                case 0xA355:
                    if (body.Length != 4) throw new InvalidDataException("主频道请求长度无效。");
                    await SendAsync(session, 0xA356, new TdrWriter().U16(GameWire.ZoneSuccess).U16(1).U16(1).U16(NormalMainChannelType)
                        .String("服务器1", 32).U32(100).U32(online.Count).U32(GameWire.Address(publicAddress))
                        .U32(GameWire.Address(publicAddress)).U32(GameWire.Address(publicAddress)), cancellationToken);
                    break;
                case 0xA132:
                    var channelReader = new TdrReader(body);
                    var mainChannel = channelReader.U32(); channelReader.End();
                    await SendAsync(session, 0xA133, new TdrWriter().U8(1).U16(1).U32(mainChannel)
                        .U32(GameWire.Address(publicAddress)).String(publicAddress, 128).U16(zonePort).U32(1)
                        .U32(1).String("频道1", 32).U32(100).U32(online.Count).U32(0).U32(0)
                        .U32(online.Count).U32(0).U32(0), cancellationToken);
                    break;
                case 0xA303:
                    lobbyMarkers.TryGetValue(session, out var markers);
                    lobbyMarkers[session] = markers + 1;
                    if (markers == 1)
                    {
                        var operation = new TdrWriter().U16(0).U64(player.CurrentBag).U64(1).U8(12).ToArray();

                        await SendAsync(session, 0xA009, new TdrWriter().U16(GameWire.ZoneSuccess).Bytes(operation), cancellationToken);
                        await SendAsync(session, 0xA00A, new TdrWriter().Bytes(operation), cancellationToken);
                    }
                    break;
                case 0xA008: case 0xA200:
                    await OperatePropAsync(session, player, command, body, cancellationToken);
                    break;
                case 0xA361:
                    var hash = new TdrReader(body); var value = hash.U32(); hash.End();
                    await SendAsync(session, 0xA362, new TdrWriter().U16(0x8200).U32(value).String(""), cancellationToken);
                    break;
                case 0xA503:
                    await SendAsync(session, 0xA504, new TdrWriter().U16(0x8200), cancellationToken);
                    break;
                case 0xA50E:
                    if (body.Length != 8) throw new InvalidDataException("AP 余额请求必须为 8 字节。");
                    // Do not use the request UIN as lookup identity. player has already been reloaded from MongoDB for the authenticated session.
                    await SendWalletAsync(session, player, cancellationToken);
                    break;
                case 0xA505:
                    await PurchaseAsync(session, player, body, cancellationToken);
                    break;
                default:
                    if (!await HandleProgressionAsync(session, player, command, body, cancellationToken) &&
                        !await HandleRoomAsync(session, player, command, body, cancellationToken, receivedAt))
                        await repository.RecordUnhandledAsync(session.Uin, magic, command, body, cancellationToken);
                    break;
            }
        }
        finally { dispatchGate.Release(); }
    }

    public async ValueTask DisconnectAsync(GameSession session, CancellationToken cancellationToken)
    {
        // Revoke session eligibility before awaiting cancelable room cleanup so a disconnected session cannot redeem an old grant.
        session.MarkDisconnected();
        await dispatchGate.WaitAsync(cancellationToken);
        try
        {
            if (online.GetValueOrDefault(session.Uin) == session) online.Remove(session.Uin);
            pendingProfiles.Remove(session);
            lobbyMarkers.Remove(session);
            settlements.Remove(session);
            await LeaveRoomAsync(session, 0, cancellationToken);
        }
        finally { dispatchGate.Release(); }
    }

    private static ValueTask SendAsync(GameSession session, ushort command, TdrWriter body, CancellationToken cancellationToken)
        => session.SendAsync(GameWire.Packet(command, body), cancellationToken);
}
