using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AFConnect.Proxy;

/// <summary>
/// AFDEV control messages for the same authenticated ZONE TLS stream. Identifiers alone do not grant round
/// eligibility.
/// </summary>
public enum AfdevControlKind : byte
{
    Prepare = 1,
    Prepared = 2,
    Redeem = 3,
    Accepted = 4,
    Revoke = 5,
    Preparation = 6,
    CancelPreparation = 7
}

/// <summary>Preparation progress carries no grant and cannot activate the datagram channel.</summary>
public enum AfdevPreparationStage : byte
{
    Queued = 1, Copying = 2, Starting = 3, Retrying = 4, Connecting = 5,
    Cancelled = 6, Failed = 7, Completed = 8, Cancelling = 9
}

/// <summary>
/// The decoded result borrows the input buffer. Secrets are valid only during the current callback and must
/// not be logged or forwarded across connections.
/// </summary>
public readonly record struct AfdevControlMessage(AfdevControlKind Kind, Guid AllocationId, Guid AdmissionId,
    ReadOnlyMemory<byte> Secret, int LocalPort, IPEndPoint? Endpoint,
    AfdevPreparationStage Stage = default, ushort Attempt = 0, ushort MaximumAttempts = 0, ushort RemainingSeconds = 0);

/// <summary>
/// Associates transparent UE3 datagrams with the current grant. Payloads are not subject to the old DS
/// codec's 512/516-byte limits.
/// </summary>
public readonly record struct AfdevDatagramMessage(Guid AllocationId, Guid AdmissionId, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The internal CONNECT v2 AFDEV contract uses network-order UUIDs. Prepare/Prepared must confirm the local
/// listener before sending the original A11A. A trusted Connect instance must verify ownership of the
/// Redeem endpoint by the game process; Server still validates the exact session and one-time ticket.
/// </summary>
public static class AfdevTunnelProtocol
{
    private const int IdentityLength = 34;
    public const int MaximumPayloadLength = TunnelFramedStream.MaximumDatagramLength - IdentityLength;

    public static byte[] EncodeControl(AfdevControlKind kind, Guid allocationId, Guid admissionId,
        ReadOnlySpan<byte> secret = default, int localPort = 0, IPEndPoint? endpoint = null)
    {
        int extra = kind switch
        {
            AfdevControlKind.Prepare => 32,
            AfdevControlKind.Prepared => 2,
            AfdevControlKind.Redeem => 38,
            AfdevControlKind.Accepted or AfdevControlKind.Revoke or AfdevControlKind.CancelPreparation => 0,
            _ => throw new InvalidDataException("未知 AFDEV 控制消息。")
        };
        if (kind == AfdevControlKind.CancelPreparation && allocationId != admissionId)
            throw new InvalidDataException("准备取消标识不一致。");

        byte[] bytes = NewIdentity((byte)kind, allocationId, admissionId, extra);

        if (kind is AfdevControlKind.Prepare or AfdevControlKind.Redeem)
        {
            if (secret.Length != 32) throw new InvalidDataException("AFDEV ticket 长度无效。");
            secret.CopyTo(bytes.AsSpan(IdentityLength, 32));
        }
        if (kind == AfdevControlKind.Prepared)
        {
            if (localPort is < 1 or > 65535) throw new InvalidDataException("AFDEV 本地端口无效。");
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(IdentityLength), (ushort)localPort);
        }
        if (kind == AfdevControlKind.Redeem)
        {
            if (endpoint is null || endpoint.Address.AddressFamily != AddressFamily.InterNetwork ||
                !IPAddress.IsLoopback(endpoint.Address) || endpoint.Port is < 1 or > 65535)
                throw new InvalidDataException("AFDEV 客户端端点必须是已核实的 IPv4 loopback 端点。");
            endpoint.Address.GetAddressBytes().CopyTo(bytes, IdentityLength + 32);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(IdentityLength + 36), (ushort)endpoint.Port);
        }

        return bytes;
    }

    /// <summary>
    /// A fixed, bounded progress frame. Encoding operation twice only distinguishes existing grant frames;
    /// it grants no eligibility.
    /// </summary>
    public static byte[] EncodePreparation(Guid operationId, AfdevPreparationStage stage,
        ushort attempt, ushort maximumAttempts, ushort remainingSeconds)
    {
        ValidatePreparation(stage, attempt, maximumAttempts);

        byte[] bytes = NewIdentity((byte)AfdevControlKind.Preparation, operationId, operationId, 7);

        bytes[IdentityLength] = (byte)stage;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(IdentityLength + 1), attempt);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(IdentityLength + 3), maximumAttempts);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(IdentityLength + 5), remainingSeconds);
        return bytes;
    }

    private static void ValidatePreparation(AfdevPreparationStage stage, ushort attempt, ushort maximumAttempts)
    {
        if (!Enum.IsDefined(stage) || maximumAttempts == 0 || attempt > maximumAttempts)
            throw new InvalidDataException("准备阶段或尝试边界无效。");
    }

    public static AfdevControlMessage DecodeControl(ReadOnlyMemory<byte> bytes)
    {
        var (allocation, admission) = ReadIdentity(bytes.Span);

        var kind = (AfdevControlKind)bytes.Span[1];
        int expected = kind switch
        {
            AfdevControlKind.Prepare => IdentityLength + 32,
            AfdevControlKind.Prepared => IdentityLength + 2,
            AfdevControlKind.Redeem => IdentityLength + 38,
            AfdevControlKind.Accepted or AfdevControlKind.Revoke or AfdevControlKind.CancelPreparation => IdentityLength,
            AfdevControlKind.Preparation => IdentityLength + 7,
            _ => throw new InvalidDataException("未知 AFDEV 控制消息。")
        };

        if (bytes.Length != expected) throw new InvalidDataException("AFDEV 控制消息长度无效。");

        if (kind is AfdevControlKind.Preparation or AfdevControlKind.CancelPreparation && allocation != admission)
            throw new InvalidDataException("准备操作标识不一致。");
        if (kind == AfdevControlKind.Preparation)
        {
            var stage = (AfdevPreparationStage)bytes.Span[IdentityLength];
            ushort attempt = BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[(IdentityLength + 1)..]);
            ushort maximum = BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[(IdentityLength + 3)..]);
            ushort remaining = BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[(IdentityLength + 5)..]);

            ValidatePreparation(stage, attempt, maximum);
            return new(kind, allocation, admission, ReadOnlyMemory<byte>.Empty, 0, null, stage, attempt, maximum, remaining);
        }

        ReadOnlyMemory<byte> secret = kind is AfdevControlKind.Prepare or AfdevControlKind.Redeem
            ? bytes.Slice(IdentityLength, 32) : ReadOnlyMemory<byte>.Empty;
        int port = kind == AfdevControlKind.Prepared
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[IdentityLength..]) : 0;
        IPEndPoint? endpoint = kind == AfdevControlKind.Redeem
            ? new IPEndPoint(new IPAddress(bytes.Span.Slice(IdentityLength + 32, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[(IdentityLength + 36)..])) : null;

        if ((kind == AfdevControlKind.Prepared && port == 0) ||
            (endpoint is not null && (!IPAddress.IsLoopback(endpoint.Address) || endpoint.Port == 0)))
            throw new InvalidDataException("AFDEV 控制端点无效。");

        return new(kind, allocation, admission, secret, port, endpoint);
    }

    public static byte[] EncodeDatagram(Guid allocationId, Guid admissionId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumPayloadLength)
            throw new InvalidDataException("AFDEV 数据报长度无效。");

        byte[] bytes = NewIdentity(0, allocationId, admissionId, payload.Length);

        payload.CopyTo(bytes.AsSpan(IdentityLength));
        return bytes;
    }

    public static AfdevDatagramMessage DecodeDatagram(ReadOnlyMemory<byte> bytes)
    {
        var (allocation, admission) = ReadIdentity(bytes.Span);

        if (bytes.Span[1] != 0 || bytes.Length <= IdentityLength ||
            bytes.Length > TunnelFramedStream.MaximumDatagramLength)
            throw new InvalidDataException("AFDEV 数据报长度或类别无效。");
        return new(allocation, admission, bytes[IdentityLength..]);
    }

    private static byte[] NewIdentity(byte kind, Guid allocation, Guid admission, int extra)
    {
        if (allocation == Guid.Empty || admission == Guid.Empty)
            throw new InvalidDataException("AFDEV allocation 或 admission 标识无效。");

        byte[] bytes = new byte[IdentityLength + extra];

        bytes[0] = 1;
        bytes[1] = kind;
        allocation.TryWriteBytes(bytes.AsSpan(2, 16), true, out _);
        admission.TryWriteBytes(bytes.AsSpan(18, 16), true, out _);
        return bytes;
    }

    private static (Guid Allocation, Guid Admission) ReadIdentity(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < IdentityLength || bytes[0] != 1)
            throw new InvalidDataException("AFDEV 控制契约版本或长度无效。");

        var allocation = new Guid(bytes.Slice(2, 16), true);
        var admission = new Guid(bytes.Slice(18, 16), true);

        if (allocation == Guid.Empty || admission == Guid.Empty)
            throw new InvalidDataException("AFDEV allocation 或 admission 标识无效。");
        return (allocation, admission);
    }
}
