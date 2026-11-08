using System.Text.Json.Serialization;
using AFConnect.Proxy;

namespace AFConnect.EDGE;

/// <summary>Defines the fixed JSON type set for Connect configuration and embedded resources.</summary>
internal static class ConnectJson
{
    internal static AotJsonSerializer Serializer { get; } = new(ConnectJsonContext.Default);
}

[JsonSerializable(typeof(ConnectOptions))]
internal partial class ConnectJsonContext : JsonSerializerContext;
