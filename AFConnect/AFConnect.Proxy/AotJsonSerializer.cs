using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AFConnect.Proxy;

/// <summary>
/// Uses compile-time JSON metadata with the caller's naming, indentation, and field-validation settings.
/// Never falls back to reflection.
/// </summary>
public sealed class AotJsonSerializer(params IJsonTypeInfoResolver[] resolvers)
{
    private readonly IJsonTypeInfoResolver resolver = JsonTypeInfoResolver.Combine([.. resolvers, PrimitiveJsonContext.Default]);
    private readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> configured = new();
    private readonly JsonSerializerOptions defaults = new();

    private JsonSerializerOptions Options(JsonSerializerOptions? options) => configured.GetValue(options ?? defaults, source =>
    {
        var result = new JsonSerializerOptions(source) { TypeInfoResolver = resolver };

        result.MakeReadOnly();
        return result;
    });

    private JsonTypeInfo<T> Info<T>(JsonSerializerOptions? options) => (JsonTypeInfo<T>)Options(options).GetTypeInfo(typeof(T));

    public string Serialize<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(value, Info<T>(options));
    public byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToUtf8Bytes(value, Info<T>(options));
    public JsonElement SerializeToElement<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToElement(value, Info<T>(options));
    public JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToNode(value, Info<T>(options));
    public void Serialize<T>(Stream stream, T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(stream, value, Info<T>(options));
    public Task SerializeAsync<T>(Stream stream, T value, JsonSerializerOptions? options, CancellationToken token) => JsonSerializer.SerializeAsync(stream, value, Info<T>(options), token);
    public T? Deserialize<T>(string json, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(json, Info<T>(options));
    public T? Deserialize<T>(ReadOnlySpan<byte> json, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(json, Info<T>(options));
    public T? Deserialize<T>(Stream stream, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(stream, Info<T>(options));
    public T? Deserialize<T>(JsonElement json, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(json, Info<T>(options));
}

[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class PrimitiveJsonContext : JsonSerializerContext;
