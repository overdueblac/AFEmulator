namespace AFConnect.Proxy;

/// <summary>
/// Copies transparently in both directions. After normal EOF, allows bounded time for the other direction
/// to drain responses; stops immediately on failure or cancellation.
/// </summary>
public static class DuplexRelay
{
    private static readonly TimeSpan GracefulDrainTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(Stream first, Stream second, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task outbound = first.CopyToAsync(second, lifetime.Token);
        Task inbound = second.CopyToAsync(first, lifetime.Token);
        Task completed = await Task.WhenAny(outbound, inbound);

        if (completed.IsFaulted || completed.IsCanceled)
        {
            await lifetime.CancelAsync();
        }
        else
        {
            Task other = ReferenceEquals(completed, outbound) ? inbound : outbound;
            Task timeout = Task.Delay(GracefulDrainTimeout, cancellationToken);

            await Task.WhenAny(other, timeout);
            await lifetime.CancelAsync();
        }
        try { await Task.WhenAll(outbound, inbound); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        // Preserve a transport failure even if the other direction observed cancellation first.
        await completed;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
