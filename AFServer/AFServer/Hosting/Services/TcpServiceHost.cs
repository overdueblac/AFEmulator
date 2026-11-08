using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Manages listener and connection lifetimes, publishing readiness only after every port binds
/// successfully.
/// </summary>
public sealed class TcpServiceHost(ServerLog log) : IAsyncDisposable
{
    private readonly List<(TcpListener Listener, string Name, Func<NetworkStream, CancellationToken, Task> Handler)> _listeners = [];
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _capacity = new(128);
    private Task[] _acceptTasks = [];
    private long _connectionId;
    private bool _disposed;

    public void Bind(IPAddress address, int port, string name, Func<NetworkStream, CancellationToken, Task> handler)
    {
        var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };

        listener.Start(128);
        _listeners.Add((listener, name, handler));
        log.Write(name, $"监听 {address}:{port}");
    }

    public void Start() => _acceptTasks = _listeners.Select(item => AcceptAsync(item.Listener, item.Name, item.Handler)).ToArray();

    private async Task AcceptAsync(TcpListener listener, string name, Func<NetworkStream, CancellationToken, Task> handler)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await _capacity.WaitAsync(_stopping.Token);

                TcpClient client;

                try { client = await listener.AcceptTcpClientAsync(_stopping.Token); }
                catch { _capacity.Release(); throw; }
                client.NoDelay = true;

                var id = Interlocked.Increment(ref _connectionId);

                log.Write(name, $"连接进入：{client.Client.RemoteEndPoint}");

                var task = ServeAsync(client, name, handler);

                _connections[id] = task;
                _ = task.ContinueWith(_ => _connections.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (SocketException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, string name, Func<NetworkStream, CancellationToken, Task> handler)
    {
        using (client)
        {
            try { await handler(client.GetStream(), _stopping.Token); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            catch (Exception ex)
            {
                log.Write(name, $"连接结束：{ex.GetType().Name}: {ex.Message}");
            }
            finally { _capacity.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _stopping.CancelAsync();
        foreach (var item in _listeners) item.Listener.Stop();
        await Task.WhenAll(_acceptTasks);
        await Task.WhenAll(_connections.Values);
        _capacity.Dispose();
        _stopping.Dispose();
    }
}
