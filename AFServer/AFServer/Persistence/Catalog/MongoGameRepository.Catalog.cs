using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AFServer.AssaultFire.Persistence;

// Authoritative product loading/explicit import shares the player-save repository instance.
public sealed partial class MongoGameRepository
{
    public async ValueTask<JsonObject> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        var document = await database.Database.GetCollection<BsonDocument>("catalogs")
            .Find(new BsonDocument("_id", "legacy-v143b")).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("MongoDB 商品目录未初始化，请执行 --import-legacy。");

        return JsonNode.Parse(document["data"].AsBsonDocument.ToJson(JsonSettings))!.AsObject();
    }

    public async Task ImportCatalogAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var data = BsonDocument.Parse(json);

        await database.Database.GetCollection<BsonDocument>("catalogs").UpdateOneAsync(
            new BsonDocument("_id", "legacy-v143b"), new BsonDocument("$setOnInsert", new BsonDocument
            {
                { "data", data }, { "imported_at", DateTime.UtcNow },
                { "source_sha256", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))) }
            }), new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    /// <summary>
    /// Explicitly publishes product catalogs with old-content concurrency checks, changing only catalog
    /// documents, never wallets/inventory.
    /// </summary>
    public async Task<bool> UpdateCatalogAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var source = JsonNode.Parse(json)!.AsObject();

        ValidateCatalog(source);

        var data = BsonDocument.Parse(json);
        var collection = database.Database.GetCollection<BsonDocument>("catalogs");
        var identity = new BsonDocument("_id", "legacy-v143b");
        var current = await collection.Find(identity).FirstOrDefaultAsync(cancellationToken);

        if (current is not null && current["data"].Equals(data)) return false;

        var filter = (BsonDocument)identity.DeepClone();

        if (current is not null) filter.Add("data", current["data"]);

        var update = new BsonDocument("$set", new BsonDocument
        {
            { "data", data }, { "updated_at", DateTime.UtcNow },
            { "source_sha256", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))) }
        });
        var result = await collection.UpdateOneAsync(filter, update,
            new UpdateOptions { IsUpsert = current is null }, cancellationToken);

        if (current is not null && result.MatchedCount != 1)
            throw new InvalidOperationException("商品目录已被其他维护操作修改，请重新核对后发布。");

        return true;
    }

    private static void ValidateCatalog(JsonObject catalog)
    {
        var items = catalog["shop_item_map"]?.AsObject() ?? throw new InvalidDataException("商品目录缺少物品映射。");
        var prices = catalog["shop_prices"]?.AsObject() ?? throw new InvalidDataException("商品目录缺少价格。");

        if (items.Count == 0) throw new InvalidDataException("商品目录不能为空。");

        foreach (var (key, item) in items)
        {
            if (!uint.TryParse(key, out var id) || id == 0 || !uint.TryParse(item?.ToString(), out var itemId) || itemId == 0 ||
                prices[key] is not JsonArray { Count: 2 } price || price[0]?.ToString() is not ("TP" or "GP" or "MP") ||
                price[1] is not JsonArray { Count: > 0 and <= 20 } variants ||
                variants.Any(value => !int.TryParse(value?.ToString(), out var amount) || amount < 0))
                throw new InvalidDataException($"商品 {key} 的物品或价格无效。");

            if (catalog["commodity_bundles"]?[key] is JsonArray bundle &&
                (bundle.Count == 0 || bundle[0]?.ToString() != item!.ToString() ||
                 bundle.Any(value => !uint.TryParse(value?.ToString(), out var child) || child == 0)))
                throw new InvalidDataException($"商品 {key} 的套装关系无效。");

            if (catalog["shop_stack_quantities"]?[key] is JsonArray quantities &&
                (quantities.Count != variants.Count ||
                 quantities.Any(value => !int.TryParse(value?.ToString(), out var quantity) || quantity <= 0)))
                throw new InvalidDataException($"商品 {key} 的数量档位与价格不一致。");
        }
    }
}
