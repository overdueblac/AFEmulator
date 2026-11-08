using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace AFConnect.Proxy;

/// <summary>
/// Exposes raw game bytes over authenticated CONNECT v2, with one logical stream per TLS connection. One
/// background reader continues dispatching auxiliary frames while game handlers pause, allowing control
/// ACKs to arrive. A full bounded game queue terminates the exact connection. Auxiliary callbacks execute
/// serially with direct backpressure, and writes are serialized. Auxiliary frames require a configured
/// handler. Any I/O failure or cancellation after a frame starts permanently terminates this adapter so
/// later calls cannot consume a partial frame.
/// </summary>
public sealed class TunnelFramedStream : Stream
{
    /// <summary>
    /// Fixed header: NZ marker, frame kind, reserved byte, four-byte big-endian length, and 16-byte
    /// network-order UUID.
    /// </summary>
    public const int HeaderLength = 24;

    /// <summary>Maximum bytes in one game fragment. Larger writes are split in order.</summary>
    public const int MaximumGameLength = 32768;

    /// <summary>Maximum bytes in one control payload.</summary>
    public const int MaximumControlLength = 4096;

    /// <summary>Maximum bytes in one IPv4 UDP payload.</summary>
    public const int MaximumDatagramLength = 65507;

    /// <summary>
    /// Buffers at most 64 game fragments, or 2 MiB. Reject a full queue to prevent ACK deadlock while
    /// waiting for the game handler.
    /// </summary>
    public const int MaximumQueuedGameFrames = 64;

    private readonly Stream transport;
    private readonly TunnelFrameHandler? auxiliaryHandler;
    private readonly bool leaveOpen;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> incomingGame = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaximumQueuedGameFrames)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly object pumpGate = new();
    private Task? readPump;
    private ExceptionDispatchInfo? readFailure;
    private byte[] bufferedGame = [];
    private int bufferedOffset;
    private int disposed;

    /// <summary>
    /// Uses a confirmed v2 negotiation result. The caller owns outer TLS authentication and establishes the
    /// business handler's provenance.
    /// </summary>
    public TunnelFramedStream(Stream transport, TunnelNegotiation negotiation,
        TunnelFrameHandler? auxiliaryHandler = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(negotiation);
        if (!transport.CanRead || !transport.CanWrite || negotiation.Version != TunnelNegotiation.FramedVersion ||
            negotiation.StreamId == Guid.Empty)
            throw new ArgumentException("CONNECT v2 需要可双向读写的传输与有效协商结果。");

        this.transport = transport;
        this.auxiliaryHandler = auxiliaryHandler;
        this.leaveOpen = leaveOpen;
        StreamId = negotiation.StreamId;
    }

    /// <summary>
    /// The server stream ID exclusive to this TLS connection, not a user identity or round grant.
    /// </summary>
    public Guid StreamId { get; }

    /// <inheritdoc />
    public override bool CanRead => Volatile.Read(ref disposed) == 0 && !lifetime.IsCancellationRequested;

    /// <inheritdoc />
    public override bool CanWrite => CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        if (buffer.Length == 0) return 0;

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);

        await readGate.WaitAsync(operation.Token);
        try
        {
            StartReading();
            while (bufferedOffset == bufferedGame.Length)
            {
                ClearBufferedGame();
                readFailure?.Throw();
                if (!await incomingGame.Reader.WaitToReadAsync(operation.Token))
                {
                    readFailure?.Throw();
                    return 0;
                }
                if (incomingGame.Reader.TryRead(out var payload)) bufferedGame = payload;
            }

            int count = Math.Min(buffer.Length, bufferedGame.Length - bufferedOffset);

            bufferedGame.AsMemory(bufferedOffset, count).CopyTo(buffer);
            bufferedOffset += count;

            return count;
        }
        catch
        {
            Stop();
            readFailure?.Throw();
            throw;
        }
        finally
        {
            if (lifetime.IsCancellationRequested) ClearBufferedGame();
            readGate.Release();
        }
    }

    /// <summary>
    /// Starts the sole transport-read loop exactly once. Install dependent session context before calling
    /// this method or the first ReadAsync.
    /// </summary>
    public void StartReading()
    {
        lock (pumpGate)
        {
            if (readPump is null) readPump = PumpAsync();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                byte[] header = new byte[HeaderLength];
                int first = await transport.ReadAsync(header.AsMemory(0, 1), lifetime.Token);

                if (first == 0) break;
                await transport.ReadExactlyAsync(header.AsMemory(1), lifetime.Token);

                var kind = (TunnelFrameKind)header[2];
                int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));

                if (header[0] != (byte)'N' || header[1] != (byte)'Z' || header[3] != 0 ||
                    new Guid(header.AsSpan(8, 16), bigEndian: true) != StreamId)
                    throw new InvalidDataException("CONNECT 帧标记、保留位或流标识无效。");
                ValidateLength(kind, length);
                if (kind != TunnelFrameKind.Game && auxiliaryHandler is null)
                    throw new InvalidDataException("当前连接尚未启用控制或数据报业务。");

                byte[] payload = new byte[length];
                bool queued = false;

                try
                {
                    await transport.ReadExactlyAsync(payload, lifetime.Token);
                    if (kind == TunnelFrameKind.Game)
                    {
                        queued = incomingGame.Writer.TryWrite(payload);
                        if (!queued) throw new IOException("CONNECT 游戏接收队列已满，连接已结束。");
                    }
                    else
                    {
                        await auxiliaryHandler!(kind, payload, lifetime.Token);
                    }
                }
                finally
                {
                    if (!queued) CryptographicOperations.ZeroMemory(payload);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            readFailure = ExceptionDispatchInfo.Capture(exception);
            Stop();
        }
        finally { incomingGame.Writer.TryComplete(); }
    }

    /// <summary>
    /// Sends a bounded auxiliary frame. Transport framing does not replace control-message authorization or
    /// datagram ownership checks.
    /// </summary>
    public ValueTask WriteAuxiliaryAsync(TunnelFrameKind kind, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (TunnelFrameKind.Control or TunnelFrameKind.Datagram))
            throw new ArgumentOutOfRangeException(nameof(kind));

        ValidateLength(kind, payload.Length);

        return WriteFramesAsync(kind, payload, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        WriteFramesAsync(TunnelFrameKind.Game, buffer, cancellationToken);

    private async ValueTask WriteFramesAsync(TunnelFrameKind kind, ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ThrowIfStopped();

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);

        await writeGate.WaitAsync(operation.Token);
        try
        {
            // Keep all fragments of one game write contiguous; other writes cannot interleave them.
            while (!buffer.IsEmpty)
            {
                int count = kind == TunnelFrameKind.Game ? Math.Min(buffer.Length, MaximumGameLength) : buffer.Length;
                byte[] header = new byte[HeaderLength];

                header[0] = (byte)'N';
                header[1] = (byte)'Z';
                header[2] = (byte)kind;
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), count);
                StreamId.TryWriteBytes(header.AsSpan(8, 16), bigEndian: true, out _);
                await transport.WriteAsync(header, operation.Token);
                await transport.WriteAsync(buffer[..count], operation.Token);
                buffer = buffer[count..];
            }
        }
        catch
        {
            Stop();
            throw;
        }
        finally { writeGate.Release(); }
    }

    private static void ValidateLength(TunnelFrameKind kind, int length)
    {
        int maximum = kind switch
        {
            TunnelFrameKind.Game => MaximumGameLength,
            TunnelFrameKind.Control => MaximumControlLength,
            TunnelFrameKind.Datagram => MaximumDatagramLength,
            _ => throw new InvalidDataException("未知 CONNECT 帧类别。")
        };

        if (length <= 0 || length > maximum) throw new InvalidDataException("CONNECT 帧载荷长度超出范围。");
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (lifetime.IsCancellationRequested) throw new IOException("CONNECT 分帧流已终止，不能继续使用。");
    }

    private void Stop() => lifetime.Cancel();

    /// <summary>
    /// Permanently aborts this logical stream and in-flight I/O without waiting for the reader. An
    /// auxiliary callback may call this if revocation cannot be delivered. The outer connection handler
    /// closes TLS afterward; do not await DisposeAsync inside the current read callback.
    /// </summary>
    public void Abort() => Stop();

    private void ClearBufferedGame()
    {
        CryptographicOperations.ZeroMemory(bufferedGame);
        bufferedGame = [];
        bufferedOffset = 0;
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopped();

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);

        await writeGate.WaitAsync(operation.Token);
        try { await transport.FlushAsync(operation.Token); }
        catch { Stop(); throw; }
        finally { writeGate.Release(); }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("仅支持异步 I/O。");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("仅支持异步 I/O。");

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException("仅支持异步 I/O。");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Cancels in-flight I/O. leaveOpen determines whether the underlying transport is closed.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Stop();
            if (!leaveOpen) transport.Dispose();
            if (readGate.Wait(0))
            {
                try
                {
                    ClearBufferedGame();
                    while (incomingGame.Reader.TryRead(out var payload)) CryptographicOperations.ZeroMemory(payload);
                }
                finally { readGate.Release(); }
            }
            // Operations may still hold buffers and semaphores until they exit; do not dispose their synchronization objects here.
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Waits for the sole reader to exit and clears queued payloads, leaving no background transport read
    /// running.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        Dispose();

        Task? pending;

        lock (pumpGate) pending = readPump;
        if (pending is not null) await pending;
        await readGate.WaitAsync();
        try
        {
            ClearBufferedGame();
            while (incomingGame.Reader.TryRead(out var payload)) CryptographicOperations.ZeroMemory(payload);
        }
        finally { readGate.Release(); }
        GC.SuppressFinalize(this);
    }
}
