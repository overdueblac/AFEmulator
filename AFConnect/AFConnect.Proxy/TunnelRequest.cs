namespace AFConnect.Proxy;

/// <summary>
/// Binds a ticket and key once per TCP tunnel that has passed mutual TLS authentication. Never log these
/// fields.
/// </summary>
public sealed record TunnelRequest(int Port, uint Uin, uint AuthType, uint ServiceId, byte[] Ticket, byte[] Key, int Mode, uint AdvertisedMode = 0);
