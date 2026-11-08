using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using AFServer.AssaultFire.Game;

namespace AFServer.AssaultFire.Persistence;

/// <summary>
/// Wallet/inventory share one MongoDB document. Version predicates prevent concurrent sessions overwriting
/// newer saves.
/// </summary>
public sealed partial class MongoGameRepository(MongoDatabaseService database) : IGameRepository
{
    private IMongoCollection<BsonDocument> Players => database.Database.GetCollection<BsonDocument>("players");
    private static readonly JsonWriterSettings JsonSettings = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    public async ValueTask<JsonObject?> LoadPlayerAsync(ulong uin, CancellationToken cancellationToken)
    {
        var document = await Players.Find(new BsonDocument("_id", checked((long)uin))).FirstOrDefaultAsync(cancellationToken);

        if (document is null) return null;

        var state = JsonNode.Parse(document["state"].AsBsonDocument.ToJson(JsonSettings))!.AsObject();

        state["_storage_revision"] = document.GetValue("revision", 0L).ToInt64();
        return state;
    }

    public async ValueTask SavePlayerAsync(ulong uin, JsonObject state, CancellationToken cancellationToken)
    {
        var revision = state["_storage_revision"]?.GetValue<long>() ?? 0;
        var copy = (JsonObject)state.DeepClone();

        copy.Remove("_storage_revision");

        var replacement = new BsonDocument
        {
            { "_id", checked((long)uin) }, { "revision", checked(revision + 1) },
            { "state", BsonDocument.Parse(copy.ToJsonString()) }, { "updated_at", DateTime.UtcNow }
        };

        if (revision == 0)
        {
            await Players.InsertOneAsync(replacement, cancellationToken: cancellationToken);
        }
        else
        {
            var result = await Players.ReplaceOneAsync(new BsonDocument
            {
                { "_id", checked((long)uin) }, { "revision", revision }
            }, replacement, cancellationToken: cancellationToken);

            if (result.ModifiedCount != 1) throw new InvalidOperationException("玩家存档版本冲突，请重新登录后重试。");
        }
        state["_storage_revision"] = revision + 1;
    }

    /// <summary>
    /// Imports create only absent documents. Repeated imports preserve purchased items and changed wallets.
    /// </summary>
    public async Task<bool> ImportPlayerAsync(ulong uin, JsonObject state, CancellationToken cancellationToken)
    {
        var result = await Players.UpdateOneAsync(new BsonDocument("_id", checked((long)uin)),
            new BsonDocument("$setOnInsert", new BsonDocument
            {
                { "revision", 1L }, { "state", BsonDocument.Parse(state.ToJsonString()) }, { "updated_at", DateTime.UtcNow }
            }), new UpdateOptions { IsUpsert = true }, cancellationToken);

        return result.UpsertedId is not null;
    }
}
