namespace AFServer.AssaultFire.Matches.Protocol;

public abstract record Ue3ActorValue;
public sealed record Ue3BoolValue(bool Value) : Ue3ActorValue;
public sealed record Ue3Int32Value(int Value) : Ue3ActorValue;
public sealed record Ue3Float32BitsValue(uint Bits) : Ue3ActorValue;
public sealed record Ue3ByteValue(byte Value) : Ue3ActorValue;
public sealed record Ue3VectorValue(Ue3QuantizedVector Value) : Ue3ActorValue;
public sealed record Ue3RotatorValue(Ue3Rotator Value) : Ue3ActorValue;
public sealed record Ue3ObjectReferenceValue(Ue3ObjectReference Value) : Ue3ActorValue;

public abstract record Ue3ActorFieldUpdate(long FieldObjectId);
public sealed record Ue3ActorPropertyUpdate(long PropertyObjectId, int ArrayIndex, Ue3ActorValue Value)
    : Ue3ActorFieldUpdate(PropertyObjectId);

/// <summary>
/// IsPresent=false means a non-Bool presence bit is zero or a parameter is unserializable; Values must then
/// be empty. Missing parameters receive no synthesized defaults. Bool has no separate presence bit and must
/// transmit all ArrayDimension values.
/// </summary>
public sealed record Ue3RpcArgument(bool IsPresent, IReadOnlyList<Ue3ActorValue> Values);
public sealed record Ue3ActorRpcCall(long FunctionObjectId, IReadOnlyList<Ue3RpcArgument> Arguments)
    : Ue3ActorFieldUpdate(FunctionObjectId);

/// <summary>
/// Fields for an open Actor channel: field ID, optional static-array index, then property values or RPC
/// arguments packed consecutively. Receiver 0x011F8520 uses direct field IDs, no increment or zero
/// terminator; bunch bit length ends the stream. Remove the open header first and bound the reader to
/// field-stream end. Full-stream failure commits no partial result. Executes no RPC and applies no client
/// properties directly to the authoritative world.
/// </summary>
public static class Ue3ActorReplicationCodec
{
    public static IReadOnlyList<Ue3ActorFieldUpdate> ReadExistingActorFields(
        ref LsbBitReader reader, Ue3ActorReplicationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var cursor = reader;
        List<Ue3ActorFieldUpdate> result = [];

        while (cursor.Remaining > 0)
        {
            if (result.Count >= Ue3ActorReplicationSchema.MaximumEntries)
                throw new InvalidDataException("Actor 字段流条目数超出服务端上限。");

            int start = cursor.Position;
            var entry = schema.ClassCache.ReadField(ref cursor);

            if (entry.Field.Kind == Ue3NetFieldKind.Property)
            {
                var property = schema.GetProperty(entry.Field.ObjectId);
                // 0x011F8F6E..0x011F8F8A uses Serialize(...,1), not SerializeInt(ArrayDim).
                int index = property.ArrayDimension == 1 ? 0 : (int)cursor.ReadBits(8);

                if (index >= property.ArrayDimension)
                    throw new InvalidDataException("Actor 属性静态数组下标越界。");
                result.Add(new Ue3ActorPropertyUpdate(property.ObjectId, index, ReadValue(ref cursor, property.ValueKind)));
            }
            else
            {
                var function = schema.GetFunction(entry.Field.ObjectId);
                var arguments = new Ue3RpcArgument[function.Parameters.Count];

                for (int i = 0; i < arguments.Length; i++)
                {
                    var parameter = function.Parameters[i];
                    bool present = parameter.CanSerialize
                        && (parameter.ValueKind == Ue3ActorValueKind.Bool || cursor.ReadBit());
                    var values = new Ue3ActorValue[present ? parameter.ArrayDimension : 0];

                    for (int j = 0; j < values.Length; j++) values[j] = ReadValue(ref cursor, parameter.ValueKind);
                    arguments[i] = new(present, Array.AsReadOnly(values));
                }
                result.Add(new Ue3ActorRpcCall(function.ObjectId, Array.AsReadOnly(arguments)));
            }
            if (cursor.Position == start)
                throw new InvalidDataException("零位字段无法在 bunch 位边界中唯一界定。");
        }
        reader = cursor;
        return result.AsReadOnly();
    }

    /// <summary>
    /// Stages the complete bounded stream before commit. Adds no terminator; the outer bunch retains its
    /// length.
    /// </summary>
    public static void WriteExistingActorFields(LsbBitWriter writer, Ue3ActorReplicationSchema schema,
        IReadOnlyList<Ue3ActorFieldUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > Ue3ActorReplicationSchema.MaximumEntries)
            throw new InvalidDataException("Actor 字段流条目数超出服务端上限。");

        var pending = new LsbBitWriter(writer.Remaining);

        foreach (var update in updates)
        {
            if (update is null) throw new InvalidDataException("Actor 字段条目为空。");
            if (update is Ue3ActorPropertyUpdate propertyIdentity && propertyIdentity.PropertyObjectId != update.FieldObjectId
                || update is Ue3ActorRpcCall functionIdentity && functionIdentity.FunctionObjectId != update.FieldObjectId)
                throw new InvalidDataException("Actor 字段头身份与属性或 RPC 身份不一致。");

            int start = pending.Position;
            var field = schema.ClassCache.FindField(update.FieldObjectId)
                ?? throw new InvalidDataException("Actor 字段身份不在缓存中。");

            schema.ClassCache.WriteFieldId(pending, update.FieldObjectId);
            if (update is Ue3ActorPropertyUpdate property && field.Field.Kind == Ue3NetFieldKind.Property)
            {
                var definition = schema.GetProperty(property.PropertyObjectId);

                if (property.ArrayIndex < 0 || property.ArrayIndex >= definition.ArrayDimension)
                    throw new InvalidDataException("Actor 属性静态数组下标越界。");
                if (definition.ArrayDimension != 1) pending.WriteBits((uint)property.ArrayIndex, 8);
                WriteValue(pending, definition.ValueKind, property.Value);
            }
            else if (update is Ue3ActorRpcCall call && field.Field.Kind == Ue3NetFieldKind.Function)
            {
                var definition = schema.GetFunction(call.FunctionObjectId);

                if (call.Arguments is null || call.Arguments.Count != definition.Parameters.Count)
                    throw new InvalidDataException("RPC 实参与运行时参数定义数量不一致。");
                for (int i = 0; i < definition.Parameters.Count; i++)
                {
                    var parameter = definition.Parameters[i];
                    var argument = call.Arguments[i];

                    if (argument is null || argument.Values is null
                        || (!parameter.CanSerialize && argument.IsPresent)
                        || (parameter.CanSerialize && parameter.ValueKind == Ue3ActorValueKind.Bool && !argument.IsPresent)
                        || argument.Values.Count != (argument.IsPresent ? parameter.ArrayDimension : 0))
                        throw new InvalidDataException("RPC 参数存在位、可序列化状态或静态数组长度无效。");
                    if (parameter.CanSerialize && parameter.ValueKind != Ue3ActorValueKind.Bool)
                        pending.WriteBit(argument.IsPresent);
                    if (argument.IsPresent)
                        foreach (var value in argument.Values) WriteValue(pending, parameter.ValueKind, value);
                }
            }
            else throw new InvalidDataException("Actor 字段条目与缓存属性/函数类型不一致。");
            if (pending.Position == start)
                throw new InvalidDataException("零位字段无法在 bunch 位边界中唯一界定。");
        }
        writer.WritePayload(pending.ToArray(), pending.Position);
    }

    private static Ue3ActorValue ReadValue(ref LsbBitReader reader, Ue3ActorValueKind kind) => kind switch
    {
        Ue3ActorValueKind.Bool => new Ue3BoolValue(Ue3PrimitivePropertyCodec.ReadBool(ref reader)),
        Ue3ActorValueKind.Int32 => new Ue3Int32Value(Ue3PrimitivePropertyCodec.ReadInt32(ref reader)),
        Ue3ActorValueKind.Float32Bits => new Ue3Float32BitsValue(Ue3PrimitivePropertyCodec.ReadFloat32Bits(ref reader)),
        Ue3ActorValueKind.Byte => new Ue3ByteValue(Ue3PrimitivePropertyCodec.ReadByte(ref reader)),
        Ue3ActorValueKind.QuantizedVector => new Ue3VectorValue(Ue3MovementValueCodec.ReadQuantizedVector(ref reader)),
        Ue3ActorValueKind.Rotator => new Ue3RotatorValue(Ue3MovementValueCodec.ReadRotator(ref reader)),
        Ue3ActorValueKind.ObjectReference => new Ue3ObjectReferenceValue(Ue3ObjectReferenceCodec.Read(ref reader)),
        _ => throw new InvalidDataException("未支持的 Actor 属性值类型。")
    };

    private static void WriteValue(LsbBitWriter writer, Ue3ActorValueKind kind, Ue3ActorValue value)
    {
        switch (kind, value)
        {
            case (Ue3ActorValueKind.Bool, Ue3BoolValue item): Ue3PrimitivePropertyCodec.WriteBool(writer, item.Value); break;
            case (Ue3ActorValueKind.Int32, Ue3Int32Value item): Ue3PrimitivePropertyCodec.WriteInt32(writer, item.Value); break;
            case (Ue3ActorValueKind.Float32Bits, Ue3Float32BitsValue item): Ue3PrimitivePropertyCodec.WriteFloat32Bits(writer, item.Bits); break;
            case (Ue3ActorValueKind.Byte, Ue3ByteValue item): Ue3PrimitivePropertyCodec.WriteByte(writer, item.Value); break;
            case (Ue3ActorValueKind.QuantizedVector, Ue3VectorValue item): Ue3MovementValueCodec.WriteQuantizedVector(writer, item.Value); break;
            case (Ue3ActorValueKind.Rotator, Ue3RotatorValue item): Ue3MovementValueCodec.WriteRotator(writer, item.Value); break;
            case (Ue3ActorValueKind.ObjectReference, Ue3ObjectReferenceValue item): Ue3ObjectReferenceCodec.Write(writer, item.Value); break;
            default: throw new InvalidDataException("Actor 属性值与已验证的属性类型不匹配。");
        }
    }
}
