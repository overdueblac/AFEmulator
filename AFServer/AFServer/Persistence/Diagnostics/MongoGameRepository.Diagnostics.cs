using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AFServer.AssaultFire.Persistence;

// Protocol diagnostics persist command metadata, lengths, and hashes only, not raw unknown requests.
public sealed partial class MongoGameRepository
{
    public async ValueTask RecordUnhandledAsync(ulong uin, ushort magic, ushort command, byte[] body, CancellationToken cancellationToken)
    {
        // Unknown requests may contain private data. Retain only fingerprints/lengths for diagnosis, never raw payloads.
        var id = $"{magic:X4}:{command:X4}:{Convert.ToHexStringLower(SHA256.HashData(body))}";
        var update = new BsonDocument
        {
            { "$set", new BsonDocument { { "last_seen_at", DateTime.UtcNow }, { "uin", checked((long)uin) } } },
            { "$setOnInsert", new BsonDocument { { "magic", magic }, { "command", command }, { "body_length", body.Length } } },
            { "$inc", new BsonDocument("count", 1L) }
        };

        await database.Database.GetCollection<BsonDocument>("unhandled_commands").UpdateOneAsync(
            new BsonDocument("_id", id), update, new UpdateOptions { IsUpsert = true }, cancellationToken);
    }
}
