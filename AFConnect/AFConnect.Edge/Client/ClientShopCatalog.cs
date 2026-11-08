using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AFServer.AssaultFire.Protocol;

namespace AFConnect.EDGE.Client;

/// <summary>
/// Completes the shop in the existing player directory and sets permanent-item types. Prices and product
/// relationships come from the catalog distributed with Server.
/// </summary>
public static class ClientShopCatalog
{
    private static readonly JsonObject Catalog = LoadCatalog();
    private static readonly HashSet<int> Disabled = Numbers(Catalog["disabled_commodities"]!.AsArray()).ToHashSet();
    private static readonly Dictionary<int, int[]> Commodities = Catalog["shop_item_map"]!.AsObject()
        .Where(pair => IsWarehouseItem(int.Parse(pair.Value!.ToString(), CultureInfo.InvariantCulture)) &&
            !Disabled.Contains(int.Parse(pair.Key, CultureInfo.InvariantCulture)))
        .ToDictionary(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture), pair =>
            Catalog["commodity_bundles"]?[pair.Key] is JsonArray bundle ? Numbers(bundle) :
                [int.Parse(pair.Value!.ToString(), CultureInfo.InvariantCulture)]);

    /// <summary>
    /// Contains two decoded configurations whose product-to-item relationships have been validated, ready
    /// for in-place writes or offline inspection.
    /// </summary>
    public sealed record Preparation(byte[] CommodityLibrary, byte[] ItemLibrary, int CommodityCount, int ItemCount);

    /// <summary>
    /// Prepares and validates both complete configurations before writing. Repeated runs leave files that
    /// already match unchanged.
    /// </summary>
    public static void Apply(string gameRoot, Action<string> log)
    {
        var directory = Path.Combine(Path.GetFullPath(gameRoot), "TGame", "Config");
        var commodityPath = Path.Combine(directory, "DefaultCommodityLibrary.ini");
        var itemPath = Path.Combine(directory, "DefaultItemLibrary.ini");
        var commodities = ClientConfigurationContainer.ReadBounded(commodityPath);
        var items = ClientConfigurationContainer.ReadBounded(itemPath);
        var prepared = Prepare(commodities, items);
        var commodityBackup = ClientConfigurationContainer.Replace(commodityPath, commodities, prepared.CommodityLibrary, "shop");
        var itemBackup = ClientConfigurationContainer.Replace(itemPath, items, prepared.ItemLibrary, "shop");

        log($"[商城] 已就绪：{prepared.CommodityCount} 个仓库商品，{prepared.ItemCount} 种物品采用永久期限；数量型商品保留数量档位。");
        if (commodityBackup is not null) log($"[商城] 商品配置备份：{commodityBackup}");
        if (itemBackup is not null) log($"[商城] 物品配置备份：{itemBackup}");
    }

    /// <summary>
    /// Transforms data in memory only. Accepts native F3 containers while preserving the client executable,
    /// skill values, and character-outfit relationships.
    /// </summary>
    public static Preparation Prepare(byte[] commodityContainer, byte[] itemContainer)
    {
        var foundCommodities = new HashSet<int>();
        var foundItems = new HashSet<int>();
        var wantedItems = Commodities.Values.SelectMany(items => items).ToHashSet();
        var commodityLibrary = Rewrite(ClientConfigurationContainer.Decode(commodityContainer),
            "UTGame.TGFPSCommodityLibrary", "RawCommodityDatas", record =>
            {
                var id = record.Number("nCommodityID");

                if (Disabled.Contains(id)) return record.Replace(new Dictionary<string, string>
                    { ["bShowCommodity"] = "false", ["bGoodsAdded"] = "false" });
                if (!Commodities.TryGetValue(id, out var bundle)) return record.Original;

                var sourceBundle = record.Numbers("nItemIDArray");
                var legacyBundle = Catalog["client_legacy_bundles"]?[id.ToString(CultureInfo.InvariantCulture)] as JsonArray;

                if (!foundCommodities.Add(id) || (!sourceBundle.SequenceEqual(bundle) &&
                    (legacyBundle is null || !sourceBundle.SequenceEqual(Numbers(legacyBundle)))))
                    throw new InvalidDataException($"商城商品 {id} 重复或套装内容与服务端不一致。");

                return record.Replace(Overrides(id));
            }, () => Catalog["client_additions"]!.AsObject()
                .Where(pair => !foundCommodities.Contains(int.Parse(pair.Key, CultureInfo.InvariantCulture)))
                .Select(pair =>
                {
                    var id = int.Parse(pair.Key, CultureInfo.InvariantCulture);

                    foundCommodities.Add(id);
                    return CreateCommodity(id, pair.Value!.AsObject());
                }).ToArray());

        if (!foundCommodities.SetEquals(Commodities.Keys))
            throw new InvalidDataException("客户端缺少已登记的仓库商品，未写入商城配置。");

        var itemLibrary = Rewrite(ClientConfigurationContainer.Decode(itemContainer),
            "UTGame.TGFPSItemLibrary", "RawItemDatas", record =>
            {
                var id = record.Number("nItemID");

                if (!wantedItems.Contains(id)) return record.Original;
                if (!foundItems.Add(id)) throw new InvalidDataException($"客户端物品 {id} 重复。");

                // Inventory processes ConsumeType=1 as a date first; expired or invalid dates display only one hour.
                // Use ConsumeType=0 for existing permanent items. Durability-based weapons and quantity-based consumables retain their native types.
                if (Catalog["client_item_additions"]?[id.ToString(CultureInfo.InvariantCulture)] is JsonObject definition)
                {
                    var fields = ItemFields(definition);

                    if (record.Value("ItemName") != fields["ItemName"])
                        throw new InvalidDataException($"新增物品 {id} 与客户端现有物品名称冲突。");
                    return record.Replace(fields);
                }

                return record.Contains("ConsumeType") && record.Number("ConsumeType") == 1
                    ? record.Replace(new Dictionary<string, string> { ["ConsumeType"] = "0" })
                    : record.Original;
            }, () => (Catalog["client_item_additions"] as JsonObject ?? [])
                .Where(pair => !foundItems.Contains(int.Parse(pair.Key, CultureInfo.InvariantCulture)))
                .Select(pair =>
                {
                    int id = int.Parse(pair.Key, CultureInfo.InvariantCulture);

                    if (!wantedItems.Contains(id)) throw new InvalidDataException($"新增物品 {id} 没有关联的商城商品。");
                    foundItems.Add(id);
                    return CreateItem(id, pair.Value!.AsObject());
                }).ToArray());

        if (!foundItems.SetEquals(wantedItems)) throw new InvalidDataException("客户端物品表不完整，未写入商城配置。");

        return new Preparation(commodityLibrary, itemLibrary, foundCommodities.Count, foundItems.Count);
    }

    private static Dictionary<string, string> Overrides(int id)
    {
        var key = id.ToString(CultureInfo.InvariantCulture);
        var price = Catalog["shop_prices"]![key]!.AsArray();
        var quantities = Catalog["shop_stack_quantities"]?[key] as JsonArray;

        return new Dictionary<string, string>
        {
            ["sCommodityType"] = Quote(price[0]!.GetValue<string>()),
            ["nItemIDArray"] = ArrayValue(Commodities[id]),
            ["nPriceArray"] = ArrayValue(Numbers(price[1]!.AsArray())),
            ["nAvailPreiodArray"] = quantities is null ? "(-1)" : ArrayValue(Numbers(quantities)),
            ["bStackProp"] = quantities is null ? "false" : "true",
            ["bShowCommodity"] = "true", ["bGoodsAdded"] = "true", ["bDisplayCommodity"] = "false",
            ["ItemOnSaleDate"] = "20110419", ["ItemOnSaleTime"] = "0",
            ["ItemRecallDate"] = "21120101", ["ItemRecallTime"] = "0",
            ["SpecialCommodityType"] = "0", ["nGroupID"] = "0", ["bFestival"] = "false"
        };
    }

    private static string CreateCommodity(int id, JsonObject addition)
    {
        var key = id.ToString(CultureInfo.InvariantCulture);
        var item = Commodities[id].Single();
        var name = Catalog["commodity_names"]![key]!.GetValue<string>();
        var location = Catalog["item_default_locations"]![item.ToString(CultureInfo.InvariantCulture)]!.ToString();
        var row = $"RawCommodityDatas=(nCommodityID={id}, IconID={addition["icon_id"]}, nGroupID=0, " +
            $"sItemName={Quote(name)}, nSaleTag=1, ItemOnSaleDate=20110419, ItemOnSaleTime=0, ItemRecallDate=21120101, ItemRecallTime=0, " +
            $"sCommodityType=\"GP\", nPriceArray=(0), nAvailPreiodArray=(-1), nMemberRebate=100, nCommonRebate=100, " +
            $"nSaleType={addition["sale_type"]}, nSaleSubType={addition["sale_sub_type"]}, nThreeLevelType={addition["three_level_type"]?.ToString() ?? "5"}, " +
            $"nItemIDArray=({item}), nItemLocation={location}, nBuyGrade=0, nBuyLimitedNum=0, SpecialCommodityType=0, " +
            "bGiveAndAskable=(false,false), bCartAdded=false, bGoodsAdded=true, bStackProp=false, bCanTryWeapon=false, " +
            "bShowCommodity=true, bDisplayCommodity=false, nOriginalPrice=0, sItemGetPath=\"\", bFestival=false, " +
            $"sItemDescription={Quote(addition["description"]!.GetValue<string>())}, sItemComment=\"\", " +
            "MPReplaceNZArray=(), SalesQuantity=1, RecommandCommodityArray=())";

        return new ClientIniRecord(EncodeLine(row)).Replace(Overrides(id));
    }

    // Add only the legacy RawItemDatas fields explicitly declared by the catalog; the old engine cannot consume newer ItemDatas structures directly.
    private static string CreateItem(int id, JsonObject definition)
    {
        var fields = ItemFields(definition);

        fields.Add("nItemID", id.ToString(CultureInfo.InvariantCulture));
        return "RawItemDatas=(" + string.Join(", ", fields.Select(pair => pair.Key + "=" + pair.Value)) + ")";
    }

    private static Dictionary<string, string> ItemFields(JsonObject definition) => definition.ToDictionary(
        pair => pair.Key, pair => pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? EncodeLine(Quote(text)) : pair.Value?.ToJsonString() ?? throw new InvalidDataException("新增物品字段不能为空。"));

    private static string EncodeLine(string row)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        return Encoding.Latin1.GetString(encoding.GetBytes(row));
    }

    private static byte[] Rewrite(byte[] raw, string targetSection, string recordName,
        Func<ClientIniRecord, string> transform, Func<string[]> additions)
    {
        var parts = Regex.Split(Encoding.Latin1.GetString(raw), "(\\r\\n|\\n|\\r)").ToList();
        var section = "";
        var foundSection = false;
        var insertion = parts.Count;
        var newline = parts.FirstOrDefault(part => part is "\r\n" or "\n" or "\r") ?? "\r\n";

        for (var index = 0; index < parts.Count; index += 2)
        {
            var line = parts[index].Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section == targetSection) insertion = index;

                section = line[1..^1];
                foundSection |= section == targetSection;
                continue;
            }
            if (section == targetSection && line.StartsWith(recordName + "=", StringComparison.Ordinal))
                parts[index] = transform(new ClientIniRecord(parts[index]));
        }
        if (!foundSection) throw new InvalidDataException($"客户端配置缺少 [{targetSection}]。");
        if (section == targetSection) insertion = parts.Count;

        var extra = additions();

        if (extra.Length != 0) parts.Insert(insertion, newline + string.Join(newline, extra) + newline);

        return Encoding.Latin1.GetBytes(string.Concat(parts));
    }

    private static JsonObject LoadCatalog()
    {
        using var input = typeof(ClientShopCatalog).Assembly.GetManifestResourceStream("AFConnect.ShopCatalog")
            ?? throw new InvalidDataException("CONNECT 缺少商城目录资源。");
        var catalog = JsonNode.Parse(input)!.AsObject();

        // The second data revision adds only Death Hunter, using the same verified product and item format.
        if (catalog["client_catalog_revision"]?.GetValue<int>() is not (1 or 2))
            throw new InvalidDataException("CONNECT 商城目录版本不受支持。");

        return catalog;
    }

    private static bool IsWarehouseItem(int id) => id is >= 100000 and < 200000 or >= 300000 and < 400000;
    private static int[] Numbers(JsonArray values) => values.Select(value => int.Parse(value!.ToString(), CultureInfo.InvariantCulture)).ToArray();
    private static string ArrayValue(int[] values) => "(" + string.Join(',', values) + ")";
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
