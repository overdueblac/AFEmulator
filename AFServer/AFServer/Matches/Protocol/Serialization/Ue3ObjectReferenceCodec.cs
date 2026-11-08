namespace AFServer.AssaultFire.Matches.Protocol;

public enum Ue3ObjectReferenceKind { Null, ActorChannel, PackageObject }

/// <summary>
/// Wire-reference identity. PackageObject Index is the current PackageMap global index, never a package
/// export index.
/// </summary>
public readonly record struct Ue3ObjectReference(Ue3ObjectReferenceKind Kind, int Index)
{
    public static Ue3ObjectReference Null => new(Ue3ObjectReferenceKind.Null, 0);
}

/// <summary>
/// AF PackageMap::SerializeObject 0x011EB180 uses 1+channel index or 0+31-bit package-object index. Decodes
/// identity only, without proving existence, type compatibility, visibility, or authentication. Callers
/// resolve/authorize separately. Native code downgrades unmappable objects to null; this writer requires
/// explicit Null to avoid silently losing authoritative objects.
/// </summary>
public static class Ue3ObjectReferenceCodec
{
    public static Ue3ObjectReference Read(ref LsbBitReader reader)
    {
        var cursor = reader;
        Ue3ObjectReference result;

        if (cursor.ReadBit())
        {
            int channel = cursor.ReadSerializedInt(Ue3PacketCodec.ChannelMaximum);

            result = channel == 0 ? Ue3ObjectReference.Null : new(Ue3ObjectReferenceKind.ActorChannel, channel);
        }
        else
            // Exclusive bound 0x80000000 requires 31 bits; signed int.MaxValue is not the SerializeInt bound.
            result = new(Ue3ObjectReferenceKind.PackageObject, (int)cursor.ReadBits(31));
        reader = cursor;
        return result;
    }

    /// <summary>
    /// Invalid values/capacity preserve the caller's bitstream. Channel zero must be represented by
    /// explicit Null.
    /// </summary>
    public static void Write(LsbBitWriter writer, Ue3ObjectReference value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var staged = new LsbBitWriter(32);

        switch (value.Kind)
        {
            case Ue3ObjectReferenceKind.Null when value.Index == 0:
                staged.WriteBit(true);
                staged.WriteSerializedInt(0, Ue3PacketCodec.ChannelMaximum);
                break;
            case Ue3ObjectReferenceKind.ActorChannel when value.Index is > 0 and < Ue3PacketCodec.ChannelMaximum:
                staged.WriteBit(true);
                staged.WriteSerializedInt(value.Index, Ue3PacketCodec.ChannelMaximum);
                break;
            case Ue3ObjectReferenceKind.PackageObject when value.Index >= 0:
                staged.WriteBit(false);
                staged.WriteBits((uint)value.Index, 31);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), "对象引用类型或索引无效。");
        }
        writer.WritePayload(staged.ToArray(), staged.Position);
    }
}
