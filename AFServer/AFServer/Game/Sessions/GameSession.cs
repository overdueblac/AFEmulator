namespace AFServer.AssaultFire.Game;

/// <summary>
/// Connection identity and business-response interface, keeping the domain layer independent of concrete
/// encrypted transports.
/// </summary>
public sealed class GameSession(ulong uin, Func<byte[], CancellationToken, ValueTask> send, Action<string>? log = null)
{
    public ulong Uin { get; } = uin;
    /// <summary>
    /// Diagnostic identity for each authenticated connection. Changes on reconnect and cannot locate or
    /// authorize another connection's grant.
    /// </summary>
    public Guid SessionId { get; } = Guid.NewGuid();
    /// <summary>
    /// Installed only by the authenticated ZONE composition root and retains that TLS stream. ROLE and
    /// local compatibility connections leave it unset.
    /// </summary>
    public IMatchSessionTransport? MatchTransport { get; internal set; }
    public string Nickname { get; internal set; } = $"Player{uin}";
    public ulong? RoomId { get; internal set; }
    private readonly Action<string>? writeLog = log;
    private int disconnected;

    internal bool IsDisconnected => Volatile.Read(ref disconnected) != 0;

    internal void MarkDisconnected() => Interlocked.Exchange(ref disconnected, 1);

    public async ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await send(packet, cancellationToken);
        if (GameWire.TryReadOutgoing(packet, out ushort magic, out ushort command, out int bodyLength))
        {
            string status = GameWire.ResponseHead(packet) is ushort head ? $" head=0x{head:X4}" : "";

            writeLog?.Invoke($"TX UIN={Uin} {(magic == GameWire.GeoMagic ? "GEO" : "ZONE")} 命令=0x{command:X4} 名称={GameWire.Describe(magic, command)} body={bodyLength}B{status}");
        }
    }
}
