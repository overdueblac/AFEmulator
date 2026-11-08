using System.Collections.Immutable;

namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// Resolved nonreturn RPC parameter. Recompute serializability for each connection snapshot rather than
/// reuse old booleans.
/// </summary>
public sealed record Ue3ReplicationParameterDefinition(long ObjectId, Ue3ActorValueKind ValueKind,
    int ArrayDimension, ulong Flags);

public sealed record Ue3ReplicationFunctionDefinition(long ObjectId,
    IReadOnlyList<Ue3ReplicationParameterDefinition> Parameters);

/// <summary>
/// Trusted loader-provided actual class, local Children, and supported field types. Do not repeat parent
/// fields or fabricate missing production types/defaults. Unbound network fields remain explicitly rejected
/// by the codec.
/// </summary>
public sealed record Ue3ReplicationClassDefinition(long ClassObjectId, long? SuperClassObjectId,
    IReadOnlyList<Ue3RuntimeNetField> LocalChildren,
    IReadOnlyList<Ue3ActorPropertySchema> LocalProperties,
    IReadOnlyList<Ue3ReplicationFunctionDefinition> LocalFunctions);

/// <summary>
/// Effective resolved-object type/flags. ObjectId is stable local identity, not an old snapshot's wire
/// index.
/// </summary>
public sealed record Ue3ReplicationObjectDefinition(long ObjectId, Ue3ActorOpenResolvedType ResolvedType,
    long ClassObjectId, ulong ObjectFlags, uint ActorFlagsAtB4);

/// <summary>
/// Immutable single-connection replication view. Rebuild class caches, RPC parameters, and open references
/// from one PackageMap. Composes trusted metadata only, without loading objects, activating levels, or
/// creating Actors.
/// </summary>
public sealed class Ue3ReplicationSnapshot
{
    private readonly object connectionIdentity;
    private readonly ImmutableDictionary<long, Ue3ReplicationClassDefinition> definitions;
    private readonly ImmutableDictionary<long, Ue3ReplicationObjectDefinition> objects;
    private readonly ImmutableDictionary<long, Ue3ClassNetCache> caches;
    private readonly ImmutableDictionary<long, Ue3ActorReplicationSchema> schemas;

    public long Revision { get; }
    public Ue3StaticPackageMap PackageMap { get; }
    public IReadOnlyList<Ue3PackageNegotiationRange> Ranges { get; }

    internal Ue3ReplicationSnapshot(object connectionIdentity, Ue3PackageNegotiationSnapshot source,
        ImmutableDictionary<long, Ue3ReplicationClassDefinition> definitions,
        ImmutableDictionary<long, Ue3ReplicationObjectDefinition> objects,
        ImmutableDictionary<long, Ue3ClassNetCache> caches,
        ImmutableDictionary<long, Ue3ActorReplicationSchema> schemas)
    {
        this.connectionIdentity = connectionIdentity;
        this.definitions = definitions;
        this.objects = objects;
        this.caches = caches;
        this.schemas = schemas;
        Revision = source.Revision;
        PackageMap = source.PackageMap;
        Ranges = source.Ranges.ToImmutableArray();
    }

    /// <summary>
    /// Revision is comparable only within one connection; equal numbers do not prove shared provenance.
    /// </summary>
    public bool IsSameConnection(Ue3ReplicationSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(connectionIdentity, other.connectionIdentity);
    }

    public bool TryGetClassCache(long classObjectId, out Ue3ClassNetCache cache)
        => caches.TryGetValue(classObjectId, out cache!);

    public Ue3ClassNetCache GetClassCache(long classObjectId) => caches.TryGetValue(classObjectId, out var cache)
        ? cache : throw new InvalidDataException("类或其继承依赖不在当前复制快照中。");

    public Ue3ActorReplicationSchema GetSchema(long classObjectId) => schemas.TryGetValue(classObjectId, out var schema)
        ? schema : throw new InvalidDataException("类或其继承依赖没有当前复制 schema。");

    /// <summary>
    /// Returns explicit objects only when both current mapping and full inheritance caches are available.
    /// Retained metadata cannot revive unloaded objects.
    /// </summary>
    public bool TryGetObject(long objectId, out Ue3ReplicationObjectDefinition definition)
    {
        if (PackageMap.CanSerializeObject(objectId) && objects.TryGetValue(objectId, out var item)
            && caches.ContainsKey(item.ClassObjectId))
        {
            definition = item;
            return true;
        }
        definition = null!;
        return false;
    }

    public Ue3ReplicationObjectDefinition GetObject(long objectId) => TryGetObject(objectId, out var item)
        ? item : throw new InvalidDataException("对象缺少当前映射、类依赖或可信有效状态。");

    public bool IsClassOrSubclassOf(long actualClassId, long requiredClassId)
    {
        if (!definitions.ContainsKey(requiredClassId)) return false;
        for (long? id = actualClassId; id is long value && definitions.TryGetValue(value, out var item);
            id = item.SuperClassObjectId)
            if (value == requiredClassId) return true;
        return false;
    }

    /// <summary>
    /// Decodes against the current map only. The caller separately checks type, visibility, and
    /// permissions.
    /// </summary>
    public bool TryResolveStaticReference(Ue3ObjectReference reference, out long objectId)
    {
        objectId = 0;
        return reference.Kind == Ue3ObjectReferenceKind.PackageObject && reference.Index >= 0
            && PackageMap.TryGetObject(reference.Index, out objectId);
    }

    /// <summary>
    /// Rebuilds current references from stable ObjectId. Rejects old open contexts and wire references at
    /// the API boundary.
    /// </summary>
    public Ue3ActorOpenContext GetActorOpenContext(long objectId)
    {
        var item = GetObject(objectId);

        if (item.ResolvedType is not (Ue3ActorOpenResolvedType.Actor or Ue3ActorOpenResolvedType.PlayerController))
            throw new InvalidDataException("Actor open 引用对象不是已解析的 Actor 类型。");
        return new(PackageMap.GetReference(objectId), item.ResolvedType, item.ClassObjectId,
            item.ObjectFlags, item.ActorFlagsAtB4);
    }
}

/// <summary>
/// Single-connection, single-thread replication-metadata composer. Copies/validates metadata during
/// construction; Capture publishes only after full view creation. provider must belong to one fixed
/// connection and return the same immutable PackageMap/ranges per Revision. Every Capture invokes it so
/// cached data cannot hide renegotiation/disconnect failure.
/// </summary>
public sealed class Ue3ReplicationContext
{
    // Server implementation limits, not native protocol constants.
    public const int MaximumClasses = 4096;
    public const int MaximumTotalFields = Ue3ClassNetCache.MaximumFields;
    public const int MaximumTotalParameters = 65536;
    public const int MaximumObjects = Ue3StaticPackageMap.MaximumKnownObjects;
    /// <summary>
    /// Inheritance fanout copies caches/schema. Accumulate a conservative bound using accepted parent
    /// fields plus local Children.
    /// </summary>
    public const int MaximumExpandedFields = 262144;

    private readonly Func<Ue3PackageNegotiationSnapshot> captureNegotiationSnapshot;
    private readonly object connectionIdentity = new();
    private readonly ImmutableDictionary<long, Ue3ReplicationClassDefinition> definitions;
    private readonly ImmutableDictionary<long, Ue3ReplicationObjectDefinition> objects;
    private readonly ImmutableArray<long> buildOrder;
    private Ue3ReplicationSnapshot? latest;

    public Ue3ReplicationContext(Ue3PackageNegotiation negotiation,
        IReadOnlyList<Ue3ReplicationClassDefinition> classes, IReadOnlyList<Ue3ReplicationObjectDefinition> objects)
        : this(CreateProvider(negotiation), classes, objects) { }

    public Ue3ReplicationContext(Func<Ue3PackageNegotiationSnapshot> captureNegotiationSnapshot,
        IReadOnlyList<Ue3ReplicationClassDefinition> classes, IReadOnlyList<Ue3ReplicationObjectDefinition> objects)
    {
        ArgumentNullException.ThrowIfNull(captureNegotiationSnapshot);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(objects);
        if (classes.Count > MaximumClasses || objects.Count > MaximumObjects)
            throw new InvalidDataException("复制类或对象定义数量超限。");
        this.captureNegotiationSnapshot = captureNegotiationSnapshot;

        var classBuilder = ImmutableDictionary.CreateBuilder<long, Ue3ReplicationClassDefinition>();
        var objectBuilder = ImmutableDictionary.CreateBuilder<long, Ue3ReplicationObjectDefinition>();
        HashSet<long> identities = [];
        int fieldCount = 0, parameterCount = 0;

        foreach (var item in classes)
        {
            if (item is null || item.ClassObjectId <= 0 || !identities.Add(item.ClassObjectId)
                || item.SuperClassObjectId is <= 0 || item.LocalChildren is null
                || item.LocalProperties is null || item.LocalFunctions is null)
                throw new InvalidDataException("复制类定义身份重复、无效或元数据为空。");
            if (item.LocalChildren.Count > MaximumTotalFields - fieldCount
                || (long)item.LocalProperties.Count + item.LocalFunctions.Count > item.LocalChildren.Count)
                throw new InvalidDataException("复制字段定义数量超限或超过本地 Children。");
            fieldCount += item.LocalChildren.Count;

            // Validate/copy all metadata first. Unmappable generations must not let bad metadata hide behind filtering.
            var children = item.LocalChildren.ToImmutableArray();
            var childById = new Dictionary<long, Ue3RuntimeNetField>();

            foreach (var field in children)
            {
                if (field is null || field.ObjectId <= 0 || !identities.Add(field.ObjectId)
                    || !childById.TryAdd(field.ObjectId, field) || field.RuntimeNetIndex < -1
                    || !Enum.IsDefined(field.Kind) || field.ReplicationOwnerId is <= 0
                    || (field.Kind == Ue3NetFieldKind.Function && field.Flags > uint.MaxValue))
                    throw new InvalidDataException("复制本地字段含重复身份或无效运行时元数据。");
            }

            HashSet<long> bound = [];
            var properties = item.LocalProperties.ToImmutableArray();

            foreach (var property in properties)
            {
                if (property is null || !bound.Add(property.ObjectId)
                    || !childById.TryGetValue(property.ObjectId, out var field) || field.Kind != Ue3NetFieldKind.Property)
                    throw new InvalidDataException("复制属性定义没有对应本地 Property 或绑定重复。");
                Ue3ActorReplicationSchema.ValidatePropertySchema(property);
            }

            var functions = ImmutableArray.CreateBuilder<Ue3ReplicationFunctionDefinition>(item.LocalFunctions.Count);

            foreach (var function in item.LocalFunctions)
            {
                if (function is null || function.Parameters is null || !bound.Add(function.ObjectId)
                    || !childById.TryGetValue(function.ObjectId, out var field) || field.Kind != Ue3NetFieldKind.Function
                    || function.Parameters.Count > Ue3ActorReplicationSchema.MaximumRpcParameters
                    || function.Parameters.Count > MaximumTotalParameters - parameterCount)
                    throw new InvalidDataException("复制 RPC 定义无效、缺少本地 Function 或参数数量超限。");
                parameterCount += function.Parameters.Count;

                var parameters = function.Parameters.ToImmutableArray();

                foreach (var parameter in parameters)
                    if (parameter is null || parameter.ObjectId <= 0 || !identities.Add(parameter.ObjectId))
                        throw new InvalidDataException("复制 RPC 参数身份重复或无效。");
                _ = new Ue3RpcFunctionSchema(function.ObjectId, parameters.Select(p =>
                    new Ue3RpcParameterSchema(p.ObjectId, p.ValueKind, p.ArrayDimension, p.Flags, false)).ToArray());
                functions.Add(new(function.ObjectId, parameters));
            }
            classBuilder.Add(item.ClassObjectId, new(item.ClassObjectId, item.SuperClassObjectId,
                children, properties, functions.MoveToImmutable()));
        }
        definitions = classBuilder.ToImmutable();

        foreach (var definition in definitions.Values)
            foreach (var property in definition.LocalProperties)
                if (property.ReferencedClassObjectId is long targetClassId && !definitions.ContainsKey(targetClassId))
                    throw new InvalidDataException("对象属性声明的目标类缺少可信类定义。");

        var order = ImmutableArray.CreateBuilder<long>(classes.Count);
        HashSet<long> ordered = [];

        foreach (var item in definitions.Values)
        {
            var chain = new List<long>();
            HashSet<long> path = [];

            for (long? id = item.ClassObjectId; id is long value;)
            {
                if (!definitions.TryGetValue(value, out var ancestor))
                    throw new InvalidDataException("复制类继承依赖缺失；不能把未知 super 当作根类。");
                if (!path.Add(value)) throw new InvalidDataException("复制类继承存在环。");
                if (chain.Count >= Ue3ClassNetCache.MaximumInheritanceDepth)
                    throw new InvalidDataException("复制类继承深度超限。");
                chain.Add(value);
                id = ancestor.SuperClassObjectId;
            }
            for (int i = chain.Count - 1; i >= 0; i--)
                if (ordered.Add(chain[i])) order.Add(chain[i]);
        }
        buildOrder = order.MoveToImmutable();
        foreach (var item in objects)
        {
            if (item is null || item.ObjectId <= 0 || !identities.Add(item.ObjectId)
                || !definitions.ContainsKey(item.ClassObjectId) || !Enum.IsDefined(item.ResolvedType)
                || item.ResolvedType == Ue3ActorOpenResolvedType.Unresolved)
                throw new InvalidDataException("复制对象身份冲突、类型未解析或实际类依赖缺失。");
            objectBuilder.Add(item.ObjectId, item);
        }
        this.objects = objectBuilder.ToImmutable();
    }

    public Ue3ReplicationSnapshot CaptureSnapshot()
    {
        var source = captureNegotiationSnapshot()
            ?? throw new InvalidDataException("连接没有提供包协商快照。");

        ValidateSource(source);
        if (latest is not null)
        {
            if (source.Revision < latest.Revision) throw new InvalidDataException("连接包协商 Revision 回退。");
            if (source.Revision == latest.Revision)
            {
                if (!ReferenceEquals(source.PackageMap, latest.PackageMap) || !source.Ranges.SequenceEqual(latest.Ranges))
                    throw new InvalidDataException("相同 Revision 的连接包快照发生变化。");
                return latest;
            }
        }

        var caches = ImmutableDictionary.CreateBuilder<long, Ue3ClassNetCache>();
        var schemas = ImmutableDictionary.CreateBuilder<long, Ue3ActorReplicationSchema>();
        var inheritedProperties = new Dictionary<long, ImmutableArray<Ue3ActorPropertySchema>>();
        var inheritedFunctions = new Dictionary<long, ImmutableArray<Ue3RpcFunctionSchema>>();
        int expandedFields = 0;

        foreach (long classId in buildOrder)
        {
            var definition = definitions[classId];
            Ue3ClassNetCache? super = null;

            if (!source.PackageMap.CanSerializeObject(classId)
                || (definition.SuperClassObjectId is long superId && !caches.TryGetValue(superId, out super))) continue;

            int projectedFields = (super?.FieldIdExclusiveMaximum ?? 0) + definition.LocalChildren.Count;

            if (projectedFields > MaximumExpandedFields - expandedFields)
                throw new InvalidDataException("复制快照继承展开字段总数超限。");
            expandedFields += projectedFields;
            foreach (var field in definition.LocalChildren)
                if (source.PackageMap.TryGetIndex(field.ObjectId, out int index))
                {
                    var range = FindRange(source.Ranges, index);

                    if (index - range.BaseIndex != field.RuntimeNetIndex)
                        throw new InvalidDataException("字段运行时 NetIndex 与当前物理包槽不一致。");
                }

            var cache = Ue3ClassNetCache.Build(classId, definition.LocalChildren, super, source.PackageMap.CanSerializeObject)
                ?? throw new InvalidDataException("当前类映射在构造期间不一致。");
            var properties = ImmutableArray.CreateBuilder<Ue3ActorPropertySchema>();
            var functions = ImmutableArray.CreateBuilder<Ue3RpcFunctionSchema>();

            if (definition.SuperClassObjectId is long parentId)
            {
                properties.AddRange(inheritedProperties[parentId]);
                functions.AddRange(inheritedFunctions[parentId]);
            }
            properties.AddRange(definition.LocalProperties.Where(x => cache.FindField(x.ObjectId) is not null));
            foreach (var function in definition.LocalFunctions)
                if (cache.FindField(function.ObjectId) is not null)
                    functions.Add(new(function.ObjectId, function.Parameters.Select(p => new Ue3RpcParameterSchema(
                        p.ObjectId, p.ValueKind, p.ArrayDimension, p.Flags, source.PackageMap.CanSerializeObject(p.ObjectId))).ToArray()));

            var propertyArray = properties.ToImmutable();
            var functionArray = functions.ToImmutable();

            caches.Add(classId, cache);
            schemas.Add(classId, new(cache, propertyArray, functionArray));
            inheritedProperties.Add(classId, propertyArray);
            inheritedFunctions.Add(classId, functionArray);
        }

        var result = new Ue3ReplicationSnapshot(connectionIdentity, source, definitions, objects,
            caches.ToImmutable(), schemas.ToImmutable());

        latest = result;
        return result;
    }

    private static Ue3PackageNegotiationRange FindRange(IReadOnlyList<Ue3PackageNegotiationRange> ranges, int index)
    {
        int low = 0, high = ranges.Count - 1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            var range = ranges[mid];

            if (index < range.BaseIndex) high = mid - 1;
            else if (index >= range.BaseIndex + range.ObjectCount) low = mid + 1;
            else return range;
        }
        throw new InvalidDataException("当前 PackageMap 对象不在声明范围内。");
    }

    private static void ValidateSource(Ue3PackageNegotiationSnapshot source)
    {
        if (source.Revision < 0 || source.PackageMap is null || source.Ranges is null
            || source.Ranges.Count > Ue3StaticPackageMap.MaximumPackages)
            throw new InvalidDataException("连接包协商快照无效。");

        long indexBase = 0;
        HashSet<long> ids = [];
        HashSet<Ue3PackageGuid> guids = [];

        foreach (var range in source.Ranges)
        {
            if (range is null || range.PackageId <= 0 || !ids.Add(range.PackageId) || !guids.Add(range.Guid)
                || range.BaseIndex != indexBase || range.ObjectCount < 0
                || !Enum.IsDefined(range.State) || range.State == Ue3PackageNegotiationState.AwaitingHave
                || range.LocalGeneration <= 0 || (range.State == Ue3PackageNegotiationState.Available && range.RemoteGeneration <= 0))
                throw new InvalidDataException("连接包范围无效、重复或尚未协商。");
            indexBase += range.ObjectCount;
        }
        if (indexBase != source.PackageMap.IndexExclusiveMaximum)
            throw new InvalidDataException("连接包范围与 PackageMap 容量不一致。");
    }

    private static Func<Ue3PackageNegotiationSnapshot> CreateProvider(Ue3PackageNegotiation negotiation)
    {
        ArgumentNullException.ThrowIfNull(negotiation);

        Ue3PackageNegotiationSnapshot? cached = null;

        return () =>
        {
            if (!negotiation.CanCaptureSnapshot) throw new InvalidDataException("连接仍在等待包协商。");
            if (cached is null || cached.Revision != negotiation.Revision) cached = negotiation.CaptureSnapshot();
            return cached;
        };
    }
}
