namespace Vorotex.K15.StatusLab;

// Owns exactly one long-lived observation loop. Device transitions never own
// or dispose its CTS; only application disposal does.
internal sealed class K15ProfileMonitorLifecycle : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private int _started;
    private int _disposed;
    private int _active;

    internal int StartCount { get; private set; }
    internal int ActiveMonitorCount => Volatile.Read(ref _active);

    internal void Start(Func<CancellationToken, Task> monitor)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(K15ProfileMonitorLifecycle));
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        StartCount++;
        _task = Task.Run(async () =>
        {
            Interlocked.Increment(ref _active);
            try
            {
                await monitor(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var cancellation = _cancellation;
        cancellation?.Cancel();
        if (_task is not null)
            await _task;
        _task = null;
        _cancellation = null;
        cancellation?.Dispose();
    }
}
