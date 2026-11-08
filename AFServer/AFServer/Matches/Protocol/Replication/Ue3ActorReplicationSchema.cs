namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Only properties backed by original machine-code test vectors. Byte requires a confirmed null Enum.
/// </summary>
public enum Ue3ActorValueKind { Bool, Int32, Float32Bits, Byte, QuantizedVector, Rotator, ObjectReference }

public sealed record Ue3ActorPropertySchema(long ObjectId, Ue3ActorValueKind ValueKind, int ArrayDimension = 1)
{
    /// <summary>
    /// ObjectProperty's declared target-class identity from a resolved declaration, not target-instance
    /// class or wire index. Optional for a wire-only codec, required when sending object properties,
    /// forbidden on other property kinds.
    /// </summary>
    public long? ReferencedClassObjectId { get; init; }
}

/// <summary>
/// Parameters resolved in actual UFunction property-iteration order, with Flags at property+0x48.
/// CanSerialize must use the same PackageMap snapshot; false parameters have neither presence bits nor
/// values.
/// </summary>
public sealed record Ue3RpcParameterSchema(long ObjectId, Ue3ActorValueKind ValueKind,
    int ArrayDimension, ulong Flags, bool CanSerialize);

public sealed class Ue3RpcFunctionSchema
{
    public long ObjectId { get; }
    public IReadOnlyList<Ue3RpcParameterSchema> Parameters { get; }

    /// <summary>
    /// parameters contains contiguous nonreturn parameters from native iteration only, unsorted and without
    /// locals. Parameter fields do not use ClassNetCache IDs; inherited/overridden functions must resolve
    /// to the actual receiving UFunction first.
    /// </summary>
    public Ue3RpcFunctionSchema(long objectId, IReadOnlyList<Ue3RpcParameterSchema> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (objectId <= 0) throw new ArgumentOutOfRangeException(nameof(objectId));
        if (parameters.Count > Ue3ActorReplicationSchema.MaximumRpcParameters)
            throw new InvalidDataException("RPC 参数数超出服务端上限。");

        HashSet<long> identities = [];
        var copy = new Ue3RpcParameterSchema[parameters.Count];
        int valueCount = 0;

        for (int i = 0; i < copy.Length; i++)
        {
            var item = parameters[i];

            if (item is null || item.ObjectId == objectId || !identities.Add(item.ObjectId)
                || (item.Flags & 0x480) != 0x80)
                throw new InvalidDataException("RPC 参数身份重复，或不是连续的非返回参数。");
            Ue3ActorReplicationSchema.ValidateValueSchema(item.ObjectId, item.ValueKind, item.ArrayDimension);
            valueCount += item.ArrayDimension;
            if (valueCount > Ue3ActorReplicationSchema.MaximumRpcValues)
                throw new InvalidDataException("RPC 参数数组总元素数超出服务端上限。");
            copy[i] = item;
        }
        ObjectId = objectId;
        Parameters = Array.AsReadOnly(copy);
    }
}

/// <summary>
/// Immutable wire snapshot for one open Actor channel. Field identities come from actual ClassNetCache;
/// types from resolved runtime properties. May bind only supported fields, but encountering an unbound
/// field fails immediately without guessed skipping. ObjectReference retains wire identity only, not
/// existence, class compatibility, or visibility. Handles no open, world state, replication conditions, RPC
/// direction, or permissions.
/// </summary>
public sealed class Ue3ActorReplicationSchema
{
    // The following are server resource limits, not native protocol constants.
    public const int MaximumEntries = 1024;
    public const int MaximumRpcParameters = 256;
    public const int MaximumArrayDimension = 256;
    public const int MaximumRpcValues = 4096;

    private readonly Dictionary<long, Ue3ActorPropertySchema> properties = [];
    private readonly Dictionary<long, Ue3RpcFunctionSchema> functions = [];
    public Ue3ClassNetCache ClassCache { get; }

    public Ue3ActorReplicationSchema(Ue3ClassNetCache classCache,
        IReadOnlyList<Ue3ActorPropertySchema> properties, IReadOnlyList<Ue3RpcFunctionSchema> functions)
    {
        ArgumentNullException.ThrowIfNull(classCache);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(functions);
        if ((long)properties.Count + functions.Count > Ue3ClassNetCache.MaximumFields)
            throw new InvalidDataException("Actor 字段定义数超出服务端上限。");
        ClassCache = classCache;
        foreach (var property in properties)
        {
            if (property is null) throw new InvalidDataException("Actor 属性定义为空。");
            ValidatePropertySchema(property);
            if (classCache.FindField(property.ObjectId)?.Field.Kind != Ue3NetFieldKind.Property
                || !this.properties.TryAdd(property.ObjectId, property))
                throw new InvalidDataException("Actor 属性没有对应缓存身份，或定义重复。");
        }
        foreach (var function in functions)
        {
            if (function is null || classCache.FindField(function.ObjectId)?.Field.Kind != Ue3NetFieldKind.Function
                || !this.functions.TryAdd(function.ObjectId, function))
                throw new InvalidDataException("RPC 没有对应函数缓存身份，或定义重复。");
        }
    }

    internal Ue3ActorPropertySchema GetProperty(long objectId) => properties.TryGetValue(objectId, out var value)
        ? value : throw new InvalidDataException("Actor 报文引用了尚未支持的属性定义。");
    internal Ue3RpcFunctionSchema GetFunction(long objectId) => functions.TryGetValue(objectId, out var value)
        ? value : throw new InvalidDataException("Actor 报文引用了尚未支持的 RPC 定义。");

    internal static void ValidateValueSchema(long objectId, Ue3ActorValueKind kind, int dimension)
    {
        if (objectId <= 0 || !Enum.IsDefined(kind) || dimension is < 1 or > MaximumArrayDimension)
            throw new InvalidDataException("Actor 属性类型、身份或静态数组维数尚未支持。");
    }

    internal static void ValidatePropertySchema(Ue3ActorPropertySchema property)
    {
        ValidateValueSchema(property.ObjectId, property.ValueKind, property.ArrayDimension);
        if (property.ReferencedClassObjectId is <= 0 ||
            (property.ReferencedClassObjectId.HasValue && property.ValueKind != Ue3ActorValueKind.ObjectReference))
            throw new InvalidDataException("对象属性的目标类身份无效，或标量属性错误声明了目标类。");
    }
}
