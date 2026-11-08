using MongoDB.Bson;
using MongoDB.Driver;
using AFServer.AssaultFire.Hosting;

namespace AFServer.AssaultFire.Persistence;

/// <summary>
/// The sole business-database entry point. Connection failure blocks startup; never silently falls back to
/// files or memory databases.
/// </summary>
public sealed class MongoDatabaseService : IDisposable
{
    private readonly MongoClient client;
    public IMongoDatabase Database { get; }

    public MongoDatabaseService(ServerOptions options)
    {
        MongoAotSerialization.EnsureRegistered();

        var settings = MongoClientSettings.FromConnectionString(options.MongoConnectionString);

        settings.ApplicationName = "AssaultFire.CSharp";
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(15);
        settings.MaxConnectionPoolSize = 32;
        client = new MongoClient(settings);
        Database = client.GetDatabase(options.MongoDatabase);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);

        var accounts = Database.GetCollection<BsonDocument>("accounts");

        await accounts.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("username_norm"),
            new CreateIndexOptions { Unique = true, Name = "uq_username_norm" }), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Disposes this host's MongoDB client after all connections/matches are reclaimed, allowing desktop
    /// restart.
    /// </summary>
    public void Dispose() => client.Dispose();
}
