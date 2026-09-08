using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

public sealed record RuntimeHostOptions
{
    public string RuntimeVersion { get; init; } = RuntimeContractMetadata.CurrentRuntimeVersion;
    public string SingleInstanceName { get; init; } = RuntimeContractMetadata.SingleInstanceName;
}

public sealed class RuntimeHost : IDisposable
{
    private readonly object _gate = new();
    private readonly string _runtimeVersion;
    private readonly string _singleInstanceName;
    private SingleInstanceLease? _singleInstanceLease;
    private TaskCompletionSource<bool> _stopped = NewStopSignal();
    private NativeStatusTransport _nativeStatusTransport;

    public RuntimeHost(RuntimeHostOptions? options = null)
    {
        options ??= new RuntimeHostOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SingleInstanceName);

        _runtimeVersion = options.RuntimeVersion;
        _singleInstanceName = options.SingleInstanceName;
        _nativeStatusTransport = new NativeStatusTransport(new RuntimeStateEngine(_runtimeVersion));
    }

    public bool IsRunning { get; private set; }

    public RuntimeSnapshot Snapshot => _nativeStatusTransport.Snapshot;

    public NativeStatusTransport NativeStatusTransport => _nativeStatusTransport;

    public string HandleIpcRequest(string json) =>
        RuntimeIpcProtocol.Handle(json, () => Snapshot, GetRuntimeHealth);

    internal RuntimeIpcRuntimeHealth GetRuntimeHealth() =>
        new(_runtimeVersion, IsRunning, IsRunning ? "READY" : "STOPPED");

    public NativeStatusTransportResult ApplyNativeStatusJson(string json)
    {
        lock (_gate)
        {
            return _nativeStatusTransport.Accept(json);
        }
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return true;
            }

            if (!SingleInstanceLease.TryAcquire(_singleInstanceName, out var lease))
            {
                return false;
            }

            _singleInstanceLease = lease;
            _stopped = NewStopSignal();
            _nativeStatusTransport = new NativeStatusTransport(new RuntimeStateEngine(_runtimeVersion));
            IsRunning = true;
            return true;
        }
    }

    public async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task stoppedTask;
        lock (_gate)
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("The runtime host must be started before waiting for shutdown.");
            }

            stoppedTask = _stopped.Task;
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => ((RuntimeHost)state!).Stop(),
            this);

        try
        {
            await stoppedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Stop();
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        SingleInstanceLease? lease;
        TaskCompletionSource<bool> stopped;

        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            lease = _singleInstanceLease;
            _singleInstanceLease = null;
            stopped = _stopped;
        }

        lease?.Dispose();
        stopped.TrySetResult(true);
    }

    public void Dispose() => Stop();

    private static TaskCompletionSource<bool> NewStopSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class SingleInstanceLease : IDisposable
{
    private readonly string _name;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private Thread? _ownerThread;
    private bool _acquired;
    private int _disposed;

    private SingleInstanceLease(string name)
    {
        _name = name;
    }

    public static bool TryAcquire(string name, out SingleInstanceLease? lease)
    {
        var candidate = new SingleInstanceLease(name);
        candidate._ownerThread = new Thread(new ThreadStart(candidate.OwnMutex))
        {
            IsBackground = true,
            Name = "Vorotex.K15.Runtime.SingleInstance",
        };

        candidate._ownerThread.Start();
        candidate._ready.Wait();
        if (!candidate._acquired)
        {
            candidate.Dispose();
            lease = null;
            return false;
        }

        lease = candidate;
        return true;
    }

    private void OwnMutex()
    {
        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: false, _name);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // An abandoned mutex is acquired by the current owner thread and can be reused.
            }

            _acquired = true;
            _ready.Set();
            _release.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            _acquired = false;
        }
        finally
        {
            _ready.Set();
            mutex?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        var ownerThread = _ownerThread;
        if (ownerThread is not null && ownerThread != Thread.CurrentThread)
        {
            ownerThread.Join();
        }

        _ready.Dispose();
        _release.Dispose();
    }
}
