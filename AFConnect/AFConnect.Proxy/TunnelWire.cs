using System.Buffers.Binary;
using System.Text.Json;

namespace AFConnect.Proxy;

/// <summary>
/// Bounded version negotiation with a four-byte big-endian JSON length. Version 1 continues as a raw
/// stream; version 2 requires framing.
/// </summary>
public static partial class TunnelWire
{
    private const int Version = TunnelNegotiation.RawVersion;
    private const int MaximumFrameLength = 32768;
    /// <summary>Maximum handshake-ticket length in bytes.</summary>
    public const int MaximumTicketLength = 8192;
    private sealed record RequestFrame(int Version, TunnelRequest Request);
    private sealed record ResultFrame(int Version, bool Success, string Message);
    private sealed record FramedResultFrame(int Version, bool Success, string Message, Guid StreamId);

    /// <summary>Sends a compatible v1 request using the original serialization layout.</summary>
    public static async Task WriteRequestAsync(Stream stream, TunnelRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        await WriteFrameAsync(stream, new RequestFrame(Version, request), cancellationToken);
    }

    /// <summary>
    /// Accepts only v1 requests so old callers cannot accidentally receive v2, which requires framing.
    /// </summary>
    public static async Task<TunnelRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync<RequestFrame>(stream, cancellationToken);

        if (frame.Version != Version || frame.Request is null)
            throw new InvalidDataException("Unsupported tunnel protocol version or missing request.");
        Validate(frame.Request);
        return frame.Request;
    }

    /// <summary>
    /// Explicitly requests a version without automatically reconnecting with a lower version if
    /// unsupported.
    /// </summary>
    public static async Task WriteRequestAsync(Stream stream, TunnelRequest request, int version,
        CancellationToken cancellationToken)
    {
        ValidateVersion(version);
        Validate(request);
        await WriteFrameAsync(stream, new RequestFrame(version, request), cancellationToken);
    }

    /// <summary>
    /// Reads v1/v2 requests for a compatible server. Successful parsing does not establish account
    /// authentication.
    /// </summary>
    public static async Task<TunnelHandshake> ReadHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync<RequestFrame>(stream, cancellationToken);

        ValidateVersion(frame.Version);
        if (frame.Request is null) throw new InvalidDataException("Missing tunnel request.");
        Validate(frame.Request);

        return new TunnelHandshake(frame.Version, frame.Request);
    }

    /// <summary>
    /// Returns the server-confirmed version and stream ID; v1 retains the original response structure.
    /// </summary>
    public static Task WriteResultAsync(Stream stream, TunnelNegotiation negotiation, bool success, string message,
        CancellationToken cancellationToken)
    {
        ValidateNegotiation(negotiation);
        if (message.Length > 1024) throw new ArgumentException("Tunnel result message is too long.", nameof(message));

        return negotiation.Version == Version
            ? WriteResultAsync(stream, success, message, cancellationToken)
            : WriteFrameAsync(stream, new FramedResultFrame(negotiation.Version, success, message, negotiation.StreamId), cancellationToken);
    }

    /// <summary>
    /// Requires the response version to match the requested version exactly and a nonempty server stream ID
    /// for v2.
    /// </summary>
    public static async Task<TunnelNegotiation> ReadResultAsync(Stream stream, int expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateVersion(expectedVersion);

        var frame = await ReadFrameAsync<FramedResultFrame>(stream, cancellationToken);
        var negotiation = new TunnelNegotiation(frame.Version, frame.StreamId);

        if (frame.Version != expectedVersion) throw new InvalidDataException("Tunnel version negotiation mismatch.");
        ValidateNegotiation(negotiation);
        if (!frame.Success) throw new IOException("Tunnel rejected: " + frame.Message);

        return negotiation;
    }

    private static void ValidateVersion(int version)
    {
        if (version is not (TunnelNegotiation.RawVersion or TunnelNegotiation.FramedVersion))
            throw new InvalidDataException("Unsupported tunnel protocol version.");
    }

    private static void ValidateNegotiation(TunnelNegotiation negotiation)
    {
        ValidateVersion(negotiation.Version);
        if ((negotiation.Version == Version) != (negotiation.StreamId == Guid.Empty))
            throw new InvalidDataException("Invalid tunnel stream identity.");
    }

    /// <summary>Sends the original v1 response without a stream ID.</summary>
    public static Task WriteResultAsync(Stream stream, bool success, string message, CancellationToken cancellationToken)
    {
        if (message.Length > 1024) throw new ArgumentException("Tunnel result message is too long.", nameof(message));
        return WriteFrameAsync(stream, new ResultFrame(Version, success, message), cancellationToken);
    }

    /// <summary>
    /// Throws IOException on server rejection or version mismatch. The caller must stop forwarding game
    /// traffic.
    /// </summary>
    public static async Task ReadResultAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync<ResultFrame>(stream, cancellationToken);

        if (frame.Version != Version) throw new InvalidDataException("Unsupported tunnel protocol version.");
        if (!frame.Success) throw new IOException("Tunnel rejected: " + frame.Message);
    }

    private static void Validate(TunnelRequest request)
    {
        if (request.Port is not (8000 or 9010 or 9060 or 65005 or 65006))
            throw new InvalidDataException("Unsupported TCP target port.");
        if (request.Ticket is null || request.Key is null || request.Ticket.Length > MaximumTicketLength)
            throw new InvalidDataException("Invalid ticket or key length.");
        if (request.Key.Length == 0)
        {
            bool taccMetadata = request.Port is 65005 or 65006 && request.Uin != 0;

            if (request.Ticket.Length != 0 || request.Mode != 0 || (!taccMetadata && request.Uin != 0) || request.AuthType != 0 || request.ServiceId != 0)
                throw new InvalidDataException("Unauthenticated relay metadata must be empty.");
        }
        else if (request.Key.Length != 16 || request.Mode is not (3 or 4) || request.Ticket.Length == 0 || request.Port is not (65005 or 65006))
            throw new InvalidDataException("Invalid AuthAP session metadata.");
        if (request.AdvertisedMode != 0 && request.AdvertisedMode != (uint)request.Mode)
            throw new InvalidDataException("Advertised AuthAP mode does not match the session mode.");
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T frame, CancellationToken cancellationToken)
    {
        byte[] payload = Json.SerializeToUtf8Bytes(frame);

        if (payload.Length > MaximumFrameLength) throw new InvalidDataException("Tunnel handshake exceeds the size limit.");

        byte[] length = new byte[4];

        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[4];

        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);

        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        if (length <= 0 || length > MaximumFrameLength) throw new InvalidDataException("Invalid tunnel handshake length.");

        byte[] payload = new byte[length];

        await stream.ReadExactlyAsync(payload, cancellationToken);
        try { return Json.Deserialize<T>(payload) ?? throw new InvalidDataException("Missing tunnel handshake."); }
        catch (JsonException exception) { throw new InvalidDataException("Malformed tunnel handshake.", exception); }
    }
}
