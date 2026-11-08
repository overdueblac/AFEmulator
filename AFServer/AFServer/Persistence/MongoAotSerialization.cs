using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace AFServer.AssaultFire.Persistence;

/// <summary>
/// Registers default collection serializers required for driver handshakes explicitly, avoiding reflective
/// closed-generic construction under Native AOT.
/// </summary>
internal static class MongoAotSerialization
{
    static MongoAotSerialization()
    {
        // ExpandoObjectSerializer construction queries List<object>; preserve registration order.
        BsonSerializer.RegisterSerializer(new ObjectListSerializer());
        BsonSerializer.RegisterSerializer(new ExpandoObjectSerializer());
    }

    internal static void EnsureRegistered() { }

    /// <summary>
    /// Fixed List implementation for default driver dynamic arrays, without reflective constructor/Add
    /// lookup.
    /// </summary>
    private sealed class ObjectListSerializer : EnumerableSerializerBase<List<object>, object>
    {
        protected override object CreateAccumulator() => new List<object>();
        protected override void AddItem(object accumulator, object item) => ((List<object>)accumulator).Add(item);
        protected override IEnumerable<object> EnumerateItemsInSerializationOrder(List<object> value) => value;
        protected override List<object> FinalizeResult(object accumulator) => (List<object>)accumulator;
    }
}
