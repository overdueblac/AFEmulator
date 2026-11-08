using System.Text.Json.Serialization;

namespace AFConnect.Proxy;

public static partial class TunnelWire
{
    private static readonly AotJsonSerializer Json = new(TunnelJsonContext.Default);

    [JsonSerializable(typeof(RequestFrame))]
    [JsonSerializable(typeof(ResultFrame))]
    [JsonSerializable(typeof(FramedResultFrame))]
    private partial class TunnelJsonContext : JsonSerializerContext;
}
