using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AFServer.AssaultFire.Persistence;

/// <summary>
/// Local accounts use randomly salted PBKDF2-SHA256 passwords with legacy-hash compatibility.
/// </summary>
public sealed partial class MongoAccounts(MongoDatabaseService database)
{
    private const int PasswordIterations = 600_000;
    private IMongoCollection<BsonDocument> Collection => database.Database.GetCollection<BsonDocument>("accounts");

    [GeneratedRegex("^[A-Za-z0-9._-]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public async Task<BsonDocument?> FindAsync(uint uin, CancellationToken cancellationToken) =>
        await Collection.Find(new BsonDocument("_id", (long)uin)).FirstOrDefaultAsync(cancellationToken);

    public async Task<uint> CreateAsync(string username, string password, CancellationToken cancellationToken)
    {
        username = username.Trim();
        if (!UsernamePattern().IsMatch(username)) throw new ArgumentException("用户名需为 3–24 位字母、数字、点、下划线或连字符。");
        if (password.Length is < 8 or > 72) throw new ArgumentException("密码长度需为 8–72 个字符。");

        var counters = database.Database.GetCollection<BsonDocument>("counters");

        // Allocate UINs through an independent atomic single-document counter, without replica-set-only cross-document transactions.
        await counters.UpdateOneAsync(new BsonDocument("_id", "account_uin"),
            new BsonDocument("$setOnInsert", new BsonDocument("value", 10000L)),
            new UpdateOptions { IsUpsert = true }, cancellationToken);

        var counter = await counters.FindOneAndUpdateAsync(new BsonDocument("_id", "account_uin"),
            new BsonDocument("$inc", new BsonDocument("value", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        var uin = checked((uint)counter["value"].ToInt64());
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        var account = new BsonDocument
        {
            { "_id", (long)uin }, { "uin", (long)uin }, { "username", username },
            { "username_norm", username.ToLowerInvariant() }, { "nickname", username },
            { "password_hash", new BsonBinaryData(hash) }, { "password_salt", new BsonBinaryData(salt) },
            { "password_iterations", PasswordIterations }, { "status", "active" },
            { "created_at", DateTime.UtcNow }
        };

        try { await Collection.InsertOneAsync(account, cancellationToken: cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(hash); }
        return uin;
    }

    /// <summary>
    /// Creates identity for legacy local saves lacking accounts. Has no default password and cannot
    /// authenticate by password.
    /// </summary>
    public async Task EnsureLocalIdentityAsync(uint uin, string nickname, CancellationToken cancellationToken)
    {
        await Collection.UpdateOneAsync(new BsonDocument("_id", (long)uin),
            new BsonDocument("$setOnInsert", new BsonDocument
            {
                { "uin", (long)uin }, { "username", $"local.{uin}" }, { "username_norm", $"local.{uin}" },
                { "nickname", nickname }, { "status", "active" }, { "created_at", DateTime.UtcNow },
                { "authentication", "legacy-local-client" }
            }), new UpdateOptions { IsUpsert = true }, cancellationToken);
        await database.Database.GetCollection<BsonDocument>("counters").UpdateOneAsync(
            new BsonDocument("_id", "account_uin"), new BsonDocument("$max", new BsonDocument("value", (long)uin)),
            new UpdateOptions { IsUpsert = true }, cancellationToken);
    }

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!UsernamePattern().IsMatch(username) || password.Length is < 8 or > 72) return false;

        var account = await Collection.Find(new BsonDocument("username_norm", username.ToLowerInvariant())).FirstOrDefaultAsync(cancellationToken);

        if (account is null || account.GetValue("status", "disabled") != "active" || !account.Contains("password_hash")) return false;

        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, account["password_salt"].AsByteArray,
            account["password_iterations"].AsInt32, HashAlgorithmName.SHA256, 32);

        try { return CryptographicOperations.FixedTimeEquals(candidate, account["password_hash"].AsByteArray); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }
}
