using System.Collections.Immutable;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

public sealed record RuntimeHostOptions
{
    public string RuntimeVersion { get; init; } = RuntimeContractMetadata.CurrentRuntimeVersion;
    public string SingleInstanceName { get; init; } = RuntimeContractMetadata.SingleInstanceName;
    public bool EnablePhysicalHidBackend { get; init; }
}

public sealed class RuntimeHost : IDisposable
{
    private readonly object _gate = new();
    private readonly string _runtimeVersion;
    private readonly string _singleInstanceName;
    private SingleInstanceLease? _singleInstanceLease;
    private TaskCompletionSource<bool> _stopped = NewStopSignal();
    private NativeStatusTransport _nativeStatusTransport;
    private readonly RuntimeDeviceManager _deviceManager;
    private readonly RuntimeRgbController _rgbController;
    private bool _stoppedForMutations;
    private long _nativeAuthorityGeneration;
    private bool _nativeAuthorityConnected;

    public RuntimeHost(RuntimeHostOptions? options = null) : this(options, null) { }

    internal RuntimeHost(RuntimeHostOptions? options, IK15DeviceBackend? deviceBackend)
    {
        options ??= new RuntimeHostOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SingleInstanceName);

        _runtimeVersion = options.RuntimeVersion;
        _singleInstanceName = options.SingleInstanceName;
        _nativeStatusTransport = new NativeStatusTransport(new RuntimeStateEngine(_runtimeVersion));
        _deviceManager = new RuntimeDeviceManager(deviceBackend ?? (options.EnablePhysicalHidBackend ? new WindowsK15HidBackend() : null));
        _rgbController = new RuntimeRgbController(_deviceManager);
    }

    public bool IsRunning { get; private set; }

    public RuntimeSnapshot Snapshot => _nativeStatusTransport.Snapshot;

    public NativeStatusTransport NativeStatusTransport => _nativeStatusTransport;

    internal RuntimeDeviceManager DeviceManager => _deviceManager;
    internal RuntimeRgbController RgbController => _rgbController;

    public string HandleIpcRequest(string json) =>
        RuntimeIpcProtocol.Handle(json, () => Snapshot, GetRuntimeHealth, HandleDeviceCommand, GetDeviceSnapshot, GetRgbSnapshot);

    internal RuntimeIpcRuntimeHealth GetRuntimeHealth() =>
        new(_runtimeVersion, IsRunning, IsRunning ? "READY" : "STOPPED");

    public NativeStatusTransportResult ApplyNativeStatusJson(string json)
    {
        lock (_gate)
        {
            var result = _nativeStatusTransport.Accept(json);
            if (result.Accepted) _rgbController.ApplyRuntimeState(Snapshot.State);
            return result;
        }
    }

    public NativeStatusTransportResult ApplyNativeThreadMetadataJson(string json)
    {
        lock (_gate) return _nativeStatusTransport.AcceptMetadata(json);
    }

    public NativeStatusTransportResult ApplyNativeRuntimeRecordJson(string json)
    {
        return ApplyNativeAuthorityRecordJson(json);
    }

    internal NativeStatusTransportResult ApplyNativeAuthorityRecordJson(string json)
    {
        if (json.Contains("\"k15-codex-thread-metadata/v1\"", StringComparison.Ordinal))
            return ApplyNativeThreadMetadataJson(json);
        if (json.Contains("\"k15-codex-authority-health/v1\"", StringComparison.Ordinal))
        {
            lock (_gate) return _nativeStatusTransport.AcceptAuthorityRecord(json);
        }
        return ApplyNativeStatusJson(json);
    }

    internal NativeStatusTransportResult ApplyNativeAuthorityRecordJson(long generation, string json)
    {
        lock (_gate)
        {
            if (!_nativeAuthorityConnected || generation != _nativeAuthorityGeneration)
                return new(false, Snapshot, ImmutableArray.Create("NATIVE_AUTHORITY_OLD_PRODUCER_GENERATION"));
            return ApplyNativeAuthorityRecordJsonLocked(json);
        }
    }

    private NativeStatusTransportResult ApplyNativeAuthorityRecordJsonLocked(string json)
    {
        if (json.Contains("\"k15-codex-thread-metadata/v1\"", StringComparison.Ordinal))
            return _nativeStatusTransport.AcceptMetadata(json);
        if (json.Contains("\"k15-codex-authority-health/v1\"", StringComparison.Ordinal))
            return _nativeStatusTransport.AcceptAuthorityRecord(json);
        var result = _nativeStatusTransport.Accept(json);
        if (result.Accepted) _rgbController.ApplyRuntimeState(Snapshot.State);
        return result;
    }

    internal long BeginNativeAuthorityProducerGeneration()
    {
        lock (_gate)
        {
            _nativeAuthorityGeneration++;
            _nativeAuthorityConnected = true;
            return _nativeAuthorityGeneration;
        }
    }

    internal void MarkNativeAuthorityDegraded(string reason)
    {
        lock (_gate) _nativeStatusTransport.MarkDegraded(reason);
    }

    internal void MarkNativeAuthorityAvailable()
    {
        lock (_gate) _nativeStatusTransport.MarkAvailable();
    }

    internal void MarkNativeAuthorityUnavailable(long generation = 0)
    {
        lock (_gate)
        {
            if (generation != 0 && generation != _nativeAuthorityGeneration) return;
            _nativeAuthorityConnected = false;
            _nativeStatusTransport.MarkDegraded("NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE");
        }
    }

    private RuntimeIpcProtocol.RuntimeIpcCommandResult HandleDeviceCommand(string command, string? candidateId, bool? enabled)
    {
        lock (_gate)
        {
            if (_stoppedForMutations)
                return new RuntimeIpcProtocol.RuntimeIpcCommandResult(false, "RUNTIME_STOPPED");
            var ok = command switch
            {
                "scan_devices" => ScanDevices(),
                "connect_device" => candidateId is not null && _deviceManager.Connect(candidateId),
                "disconnect_device" => DisconnectAndDisable(),
                "reconnect_device" => _deviceManager.Reconnect(),
                "set_rgb_enabled" => enabled.HasValue && _rgbController.SetEnabled(enabled.Value),
                "restore_lighting" => _rgbController.RestoreLighting(),
                _ => false,
            };
            return new RuntimeIpcProtocol.RuntimeIpcCommandResult(ok, ok ? null : _deviceManager.LastFailure ?? _rgbController.LastFailure ?? "COMMAND_FAILED");
        }
    }

    private RuntimeIpcDeviceSnapshot GetDeviceSnapshot()
    {
        var candidates = _deviceManager.Candidates.Select(candidate => new RuntimeIpcDeviceCandidate(
            candidate.CandidateId, candidate.Product, candidate.VendorId, candidate.ProductId,
            candidate.ProtocolVerified, candidate.CandidateId == _deviceManager.Selected?.CandidateId,
            candidate.CandidateId == _deviceManager.Selected?.CandidateId && _deviceManager.IsConnected)).ToImmutableArray();
        return new RuntimeIpcDeviceSnapshot(_deviceManager.ConnectionState, _deviceManager.Selected?.CandidateId,
            candidates, candidates.Length, _deviceManager.Selected?.ProtocolVerified == true, _deviceManager.LastFailure);
    }

    private RuntimeIpcRgbSnapshot GetRgbSnapshot() => new(
        _rgbController.Enabled, _rgbController.Effect, _rgbController.TransportAvailable,
        _rgbController.RestoreAvailable, _rgbController.LastFailure);

    private bool DisconnectAndDisable()
    {
        var restored = _rgbController.SetEnabled(false);
        _deviceManager.Disconnect();
        return restored;
    }

    private bool ScanDevices()
    {
        _deviceManager.Scan();
        return _deviceManager.LastFailure is null;
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
            _stoppedForMutations = false;
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
        lock (_gate)
        {
            var wasRunning = IsRunning;
            IsRunning = false;
            _stoppedForMutations = true;
            var lease = _singleInstanceLease;
            _singleInstanceLease = null;
            var stopped = _stopped;

            _rgbController.Disarm();
            _deviceManager.Dispose();
            lease?.Dispose();
            if (wasRunning) stopped.TrySetResult(true);
        }
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
