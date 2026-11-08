using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

// Shop purchases retain GameDispatcher's serialized transaction boundary and the atomic single-document wallet/inventory commit.
public sealed partial class GameDispatcher
{
    private sealed record CartRow(uint CommodityId, ushort PriceIndex, uint ClientPrice, uint VoucherId);
    private sealed record PurchaseRequest(ushort BuyType, ulong Consignee, string Nickname, CartRow[] Rows, ushort PayType, uint ConvertMp);
    private sealed record GrantedRow(CartRow Cart, List<JsonObject> Props);
    private sealed class ShopRejectedException(ushort result, string message) : Exception(message)
    {
        internal ushort Result { get; } = result;
    }

    private static PurchaseRequest ReadPurchase(byte[] body)
    {
        var reader = new TdrReader(body);
        var buyType = reader.U16(); var consignee = reader.U64(); var nickname = reader.String(32); var count = reader.U16();

        if (count > 20) throw new InvalidDataException("购物车不能超过二十项。");

        var rows = new CartRow[count];

        for (var index = 0; index < count; index++) rows[index] = new CartRow(reader.U32(), reader.U16(), reader.U32(), reader.U32());

        var payType = reader.U16(); var convertMp = reader.U32(); reader.String(256); reader.End();
        return new PurchaseRequest(buyType, consignee, nickname, rows, payType, convertMp);
    }

    private async ValueTask PurchaseAsync(GameSession session, PlayerState player, byte[] body, CancellationToken cancellationToken)
    {
        var request = ReadPurchase(body);
        var grants = new List<GrantedRow>();
        var totals = new Dictionary<string, long> { ["ap"] = 0, ["gp"] = 0, ["mp"] = 0 };
        ushort result = 0x8200;

        try
        {
            if (request.Rows.Length == 0) throw new ShopRejectedException(0x820A, "购物车为空。");
            if (request.BuyType is not (1 or 2) || (request.Consignee != 0 && request.Consignee != session.Uin))
                throw new ShopRejectedException(0x8201, "不支持该购买方式或跨账号赠送。");
            if (request.ConvertMp != 0) throw new ShopRejectedException(0x820C, "不支持点券转换。");
            foreach (var row in request.Rows)
            {
                var key = row.CommodityId.ToString();
                var prices = catalog!["shop_prices"]?[key]?.AsArray();
                var rootItem = catalog["shop_item_map"]?[key];

                if (catalog["disabled_commodities"]?.AsArray().Any(value => value!.ToString() == key) == true)
                    throw new ShopRejectedException(0x8202, "客户端不支持该商品。");
                if (prices is null || rootItem is null) throw new ShopRejectedException(0x8202, "商品不存在。");

                var variants = prices[1]!.AsArray();

                if (row.PriceIndex >= variants.Count) throw new ShopRejectedException(0x8208, "商品价格档位不存在。");

                var currency = prices[0]!.ToString() switch { "TP" => "ap", "GP" => "gp", "MP" => "mp", _ => "" };
                var payType = currency switch { "ap" => 2, "gp" => 1, "mp" => 3, _ => 0 };

                if (request.PayType != payType || payType == 0 || row.VoucherId != 0)
                    throw new ShopRejectedException(0x820C, "支付方式无效。");
                // Client prices are diagnostic only; charges always use MongoDB's authoritative product catalog.
                totals[currency] = checked(totals[currency] + long.Parse(variants[row.PriceIndex]!.ToString()));
            }
            if (totals.Any(p => p.Value > player.Balance(p.Key))) throw new ShopRejectedException(0x820B, "余额不足。");

            foreach (var row in request.Rows)
            {
                var props = CreateCommodityProps(player, row.CommodityId, row.PriceIndex);

                grants.Add(new GrantedRow(row, props));
                foreach (var prop in props) player.Inventory.Add((System.Text.Json.Nodes.JsonNode)prop);
            }
            foreach (var (currency, amount) in totals) player.Debit(currency, amount);
            // Promise a debit and item delivery to the client only after the atomic MongoDB commit succeeds.
            await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);
        }
        catch (ShopRejectedException exception)
        {
            result = exception.Result;
            grants.Clear();
            foreach (var currency in totals.Keys.ToArray()) totals[currency] = 0;
        }

        var response = new TdrWriter().U16(result).U16(request.BuyType).U16(request.PayType).String(request.Nickname, 32)
            .U32(totals["ap"]).U32(player.Balance("ap")).U32(totals["gp"]).U32(totals["mp"]).U16(grants.Count);

        foreach (var row in grants)
        {
            response.U32(row.Cart.CommodityId).U16(row.Cart.PriceIndex).U32(row.Props.Count);
            foreach (var prop in row.Props)
                response.U64(PlayerState.Number(prop, "gid")).U32((long)PlayerState.Number(prop, "item_id"))
                    .U32(InventoryLifetime.Permanent);
        }
        response.U64(request.Consignee == 0 ? session.Uin : request.Consignee);
        await SendAsync(session, 0xA506, response, cancellationToken);
        if (result == 0x8200)
        {
            await SendInventoryAsync(session, player, cancellationToken);
            await SendWalletAsync(session, player, cancellationToken);
        }
    }

    private List<JsonObject> CreateCommodityProps(PlayerState player, uint commodityId, ushort priceIndex)
    {
        var key = commodityId.ToString();
        var items = catalog!["commodity_bundles"]?[key]?.AsArray().Select(v => uint.Parse(v!.ToString())).ToArray();
        var quantities = catalog["shop_stack_quantities"]?[key]?.AsArray();
        var quantity = quantities is null ? 1U : uint.Parse(quantities[priceIndex]!.ToString());

        if (items is null || items.Length == 0) items = [uint.Parse(catalog["shop_item_map"]![key]!.ToString())];

        var props = new List<JsonObject>();
        ulong role = 0;

        foreach (var item in items)
        {
            var gid = player.AllocateGid();
            var location = DefaultLocation(item);
            var owner = location is 10 or 11 ? 0 : role;
            var prop = PlayerState.NewProp(gid, item, owner, owner != 0 ? unchecked((byte)location) : (byte)12,
                location is 10 or 11 || owner != 0 || location < 0 ? 0U : 100U);

            // Quantity-product tiers represent item counts. Do not interpret revival-coin quantities as durations or always issue only one item.
            prop["stack_num"] = quantity;
            props.Add(prop);
            if (location is 10 or 11) role = gid;
        }
        return props;
    }
}
