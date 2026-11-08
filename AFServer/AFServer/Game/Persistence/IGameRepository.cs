using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

/// <summary>
/// Game persistence boundary. Implementations must commit a player's wallet and inventory as one atomic
/// document.
/// </summary>
public interface IGameRepository
{
    ValueTask<JsonObject?> LoadPlayerAsync(ulong uin, CancellationToken cancellationToken);
    ValueTask SavePlayerAsync(ulong uin, JsonObject state, CancellationToken cancellationToken);
    ValueTask<JsonObject> LoadCatalogAsync(CancellationToken cancellationToken);
    ValueTask RecordUnhandledAsync(ulong uin, ushort magic, ushort command, byte[] body, CancellationToken cancellationToken);
}
