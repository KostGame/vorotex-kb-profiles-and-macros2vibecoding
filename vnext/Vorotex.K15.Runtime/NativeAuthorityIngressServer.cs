using System.IO.Pipes;
using System.Text;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>
/// Dedicated bridge ingress. It is intentionally separate from RuntimeIpcServer:
/// the pipe accepts only sanitized native-authority records and has no command
/// or response surface.
/// </summary>
public sealed class NativeAuthorityIngressServer : IAsyncDisposable
{
    private const int MaxProducerConnections = 8;
    private readonly RuntimeHost _host;
    private readonly string _pipeName;
    private readonly NativeStatusDeliveryQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _producerSlots = new(MaxProducerConnections, MaxProducerConnections);
    private readonly object _listenerGate = new();
    private readonly List<Task> _producers = new();
    private long _acceptedProducers;
    private long _disconnectsBeforeFirstFrame;
    private long _decodedFrames;
    private long _queueAccepted;
    private long _queueRejected;
    private long _ioFailures;
    private long _ioUnavailableFailures;
    private long _ioRemoteCloseFailures;
    private long _ioUnknownFailures;
    private NamedPipeServerStream? _listener;
    private Task? _acceptLoop;

    public NativeAuthorityIngressServer(RuntimeHost host, string? pipeName = null, int queueCapacity = 64)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pipeName = pipeName ?? RuntimeIpcMetadata.NativeAuthorityPipeName;
        _queue = new NativeStatusDeliveryQueue(
            (long generation, string json) => _host.ApplyNativeAuthorityRecordJson(generation, json),
            _host.MarkNativeAuthorityDegraded,
            queueCapacity);
    }

    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("authority ingress already started");
        _acceptLoop = AcceptLoopAsync();
    }

    public NativeAuthorityIngressHealth Health => new(
        AcceptedProducers: Volatile.Read(ref _acceptedProducers),
        DisconnectsBeforeFirstFrame: Volatile.Read(ref _disconnectsBeforeFirstFrame),
        DecodedFrames: Volatile.Read(ref _decodedFrames),
        QueueAccepted: Volatile.Read(ref _queueAccepted),
        QueueRejected: Volatile.Read(ref _queueRejected),
        IoFailures: Volatile.Read(ref _ioFailures),
        IoUnavailableFailures: Volatile.Read(ref _ioUnavailableFailures),
        IoRemoteCloseFailures: Volatile.Read(ref _ioRemoteCloseFailures),
        IoUnknownFailures: Volatile.Read(ref _ioUnknownFailures));

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _producerSlots.WaitAsync(_stop.Token).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                var handedOff = false;
                var generation = 0L;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName, PipeDirection.InOut, MaxProducerConnections, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    lock (_listenerGate) _listener = pipe;
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _acceptedProducers);
                    generation = _host.BeginNativeAuthorityProducerGeneration();
                    var producer = HandleProducerLifetimeAsync(pipe, generation);
                    lock (_listenerGate)
                    {
                        _producers.Add(producer);
                        if (ReferenceEquals(_listener, pipe)) _listener = null;
                    }
                    ObserveProducerCompletion(producer);
                    handedOff = true;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (!_stop.IsCancellationRequested)
                {
                    RecordIoFailure(remoteClose: false);
                }
                finally
                {
                    if (pipe is not null && !handedOff)
                    {
                        lock (_listenerGate)
                        {
                            if (ReferenceEquals(_listener, pipe)) _listener = null;
                        }
                        await pipe.DisposeAsync().ConfigureAwait(false);
                        _producerSlots.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleProducerLifetimeAsync(NamedPipeServerStream pipe, long generation)
    {
        var sawCompleteFrame = false;
        try
        {
            sawCompleteFrame = await HandleProducerAsync(pipe, generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (!_stop.IsCancellationRequested)
        {
            RecordIoFailure(remoteClose: true);
        }
        finally
        {
            if (!sawCompleteFrame && !_stop.IsCancellationRequested)
                Interlocked.Increment(ref _disconnectsBeforeFirstFrame);
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (!_stop.IsCancellationRequested && generation != 0)
                _host.MarkNativeAuthorityUnavailable(generation);
            _producerSlots.Release();
        }
    }

    private void ObserveProducerCompletion(Task producer)
    {
        _ = producer.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_listenerGate) _producers.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<bool> HandleProducerAsync(NamedPipeServerStream pipe, long generation)
    {
        var decoder = new BoundedJsonLineDecoder(RuntimeIpcMetadata.MaxFrameBytes);
        var sawCompleteFrame = false;
        while (!_stop.IsCancellationRequested)
        {
            var decoded = await decoder.ReadLineAsync(pipe, _stop.Token).ConfigureAwait(false);
            if (decoded is null) break;
            if (!decoded.IsComplete)
            {
                Interlocked.Increment(ref _queueRejected);
                continue;
            }
            var line = decoded.Value;
            sawCompleteFrame = true;
            Interlocked.Increment(ref _decodedFrames);
            if (line.Length > RuntimeIpcMetadata.MaxFrameBytes)
            {
                // The decoder has already consumed exactly through the line
                // terminator. Do not enqueue the sentinel: an oversized frame
                // is a bounded transport rejection, not a sink failure.
                Interlocked.Increment(ref _queueRejected);
                continue;
            }
            if (line.Length == 0 || !_queue.TryEnqueue(generation, line))
            {
                Interlocked.Increment(ref _queueRejected);
                // Invalid/oversized input is rejected without affecting the
                // Runtime/UI command pipe or transparent Codex transport.
            }
            else
            {
                Interlocked.Increment(ref _queueAccepted);
            }
        }
        return sawCompleteFrame;
    }

    private sealed class BoundedJsonLineDecoder
    {
        private readonly int _maxFrameBytes;
        private readonly Queue<DecodedLine> _ready = new();
        private readonly List<byte> _current = new();
        private bool _discardingOversized;

        public BoundedJsonLineDecoder(int maxFrameBytes) => _maxFrameBytes = maxFrameBytes;

        public async Task<DecodedLine?> ReadLineAsync(Stream pipe, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            while (_ready.Count == 0)
            {
                var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_current.Count != 0 || _discardingOversized)
                    {
                        _current.Clear();
                        _discardingOversized = false;
                        return new DecodedLine(string.Empty, IsComplete: false);
                    }
                    return null;
                }
                Feed(buffer, read);
            }
            return _ready.Dequeue();
        }

        private void Feed(byte[] buffer, int count)
        {
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (_discardingOversized)
                    {
                        _ready.Enqueue(new DecodedLine(new string('x', _maxFrameBytes + 1), IsComplete: true));
                        _discardingOversized = false;
                        _current.Clear();
                    }
                    else
                    {
                        try
                        {
                            _ready.Enqueue(new DecodedLine(StrictUtf8.GetString(_current.ToArray()).TrimEnd('\r'), IsComplete: true));
                        }
                        catch (DecoderFallbackException)
                        {
                            // An invalid frame is rejected at the frame boundary.
                            // The decoder has consumed through its newline, so the
                            // next JSONL record remains independently processable.
                            _ready.Enqueue(new DecodedLine(string.Empty, IsComplete: true));
                        }
                        _current.Clear();
                    }
                    continue;
                }

                if (_discardingOversized) continue;
                if (_current.Count == _maxFrameBytes)
                {
                    _current.Clear();
                    _discardingOversized = true;
                    continue;
                }
                _current.Add(value);
            }
        }

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public sealed record DecodedLine(string Value, bool IsComplete);
    }

    private void RecordIoFailure(bool remoteClose)
    {
        Interlocked.Increment(ref _ioFailures);
        if (remoteClose) Interlocked.Increment(ref _ioRemoteCloseFailures);
        else Interlocked.Increment(ref _ioUnavailableFailures);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_listenerGate) _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Task[] producers;
        lock (_listenerGate) producers = _producers.ToArray();
        try { await Task.WhenAll(producers).ConfigureAwait(false); } catch { }
        await _queue.DisposeAsync().ConfigureAwait(false);
        _producerSlots.Dispose();
        _stop.Dispose();
    }
}

public sealed record NativeAuthorityIngressHealth(
    long AcceptedProducers,
    long DisconnectsBeforeFirstFrame,
    long DecodedFrames,
    long QueueAccepted,
    long QueueRejected,
    long IoFailures,
    long IoUnavailableFailures,
    long IoRemoteCloseFailures,
    long IoUnknownFailures);
