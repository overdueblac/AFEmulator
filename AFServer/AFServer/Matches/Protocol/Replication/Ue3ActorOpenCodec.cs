namespace AFServer.AssaultFire.Matches.Protocol;

/// <summary>
/// New AF Actor-channel open payload syntax, only for open bunches without a bound Actor. Receiver
/// 0x011F8520 selects fields from resolved-object flags/types, not a fixed Object+Vector+Rotator header.
/// Requires trusted resolution context and reader bounds at bunch end. Failures leave readers/writers
/// unchanged. Handles no loading, spawning, channel transitions, replication conditions, or RPC execution.
/// </summary>
public static class Ue3ActorOpenCodec
{
    /// <summary>
    /// Consumes only the conditional open header, preserving subsequent field bits. Rejects
    /// null/unresolved/non-Actor references or mismatched context identity.
    /// </summary>
    public static Ue3ActorOpenHeader ReadHeader(ref LsbBitReader reader, bool isOpenBunch,
        Ue3ActorOpenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireOpen(isOpenBunch);

        var cursor = reader;
        var reference = Ue3ObjectReferenceCodec.Read(ref cursor);

        ValidateContext(context, reference);

        Ue3QuantizedVector? location = context.HasInitialLocation
            ? Ue3MovementValueCodec.ReadQuantizedVector(ref cursor) : null;
        Ue3Rotator? rotation = context.HasInitialRotation ? Ue3MovementValueCodec.ReadRotator(ref cursor) : null;
        byte? playerIndex = context.HasPlayerControllerIndex ? (byte)cursor.ReadBits(8) : null;

        reader = cursor;
        return new(reference, location, rotation, playerIndex);
    }

    /// <summary>
    /// Field presence must match data/context requirements exactly. Never inserts defaults, discards excess
    /// fields, or byte-aligns the stream.
    /// </summary>
    public static void WriteHeader(LsbBitWriter writer, bool isOpenBunch, Ue3ActorOpenContext context,
        Ue3ActorOpenHeader header)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(header);
        RequireOpen(isOpenBunch);
        ValidateContext(context, header.ObjectReference);
        if (header.InitialLocation.HasValue != context.HasInitialLocation
            || header.InitialRotation.HasValue != context.HasInitialRotation
            || header.PlayerControllerIndex.HasValue != context.HasPlayerControllerIndex)
            throw new InvalidDataException("Actor open 字段存在性与已解析对象的 flags/类型不一致。");

        var pending = new LsbBitWriter(writer.Remaining);

        Ue3ObjectReferenceCodec.Write(pending, header.ObjectReference);
        if (header.InitialLocation is { } location) Ue3MovementValueCodec.WriteQuantizedVector(pending, location);
        if (header.InitialRotation is { } rotation) Ue3MovementValueCodec.WriteRotator(pending, rotation);
        if (header.PlayerControllerIndex is { } index) pending.WriteBits(index, 8);
        writer.WritePayload(pending.ToArray(), pending.Position);
    }

    /// <summary>
    /// Atomically reads open header and all following fields for a bunch. schema must match the actual
    /// resolved/new Actor class and bound RPC parameters; reference indices cannot infer that binding.
    /// Later field failure rolls back the header too.
    /// </summary>
    public static Ue3ActorOpenPayload ReadPayload(ref LsbBitReader reader, bool isOpenBunch,
        Ue3ActorOpenContext context, Ue3ActorReplicationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ValidateSchema(context, schema);

        var cursor = reader;
        var header = ReadHeader(ref cursor, isOpenBunch, context);
        var fields = Ue3ActorReplicationCodec.ReadExistingActorFields(ref cursor, schema);

        reader = cursor;
        return new(header, fields);
    }

    /// <summary>
    /// Encodes the header/full field stream in bounded staging first. Later field/capacity failures leave
    /// no partial open header.
    /// </summary>
    public static void WritePayload(LsbBitWriter writer, bool isOpenBunch, Ue3ActorOpenContext context,
        Ue3ActorReplicationSchema schema, Ue3ActorOpenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(payload);
        ValidateSchema(context, schema);

        var pending = new LsbBitWriter(writer.Remaining);

        WriteHeader(pending, isOpenBunch, context, payload.Header);
        Ue3ActorReplicationCodec.WriteExistingActorFields(pending, schema, payload.Fields);
        writer.WritePayload(pending.ToArray(), pending.Position);
    }

    private static void RequireOpen(bool isOpenBunch)
    {
        if (!isOpenBunch) throw new InvalidDataException("尚未绑定 Actor 的通道必须通过 open bunch 读取初始载荷。");
    }

    private static void ValidateContext(Ue3ActorOpenContext context, Ue3ObjectReference reference)
    {
        bool validReference = reference.Kind == Ue3ObjectReferenceKind.ActorChannel
            ? reference.Index is > 0 and < Ue3PacketCodec.ChannelMaximum
            : reference.Kind == Ue3ObjectReferenceKind.PackageObject && reference.Index >= 0;

        if (!validReference) throw new InvalidDataException("Actor open 必须引用非空且合法的对象身份。");
        if (reference != context.ObjectReference)
            throw new InvalidDataException("Actor open 引用与可信解析上下文不属于同一对象。");
        if (context.ResolvedType is not (Ue3ActorOpenResolvedType.Actor or Ue3ActorOpenResolvedType.PlayerController))
            throw new InvalidDataException("Actor open 对象尚未解析为 Actor 或 PlayerController。");
        if (context.ActorClassObjectId <= 0)
            throw new InvalidDataException("Actor open 未提供可信的实际 Actor 类身份。");
    }

    private static void ValidateSchema(Ue3ActorOpenContext context, Ue3ActorReplicationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorClassObjectId != schema.ClassCache.ClassObjectId)
            throw new InvalidDataException("Actor open 后续字段缓存与已解析 Actor 的实际类身份不一致。");
    }
}
