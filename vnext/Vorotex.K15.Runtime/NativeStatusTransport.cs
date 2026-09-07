using System.Collections.Immutable;
using System.Threading.Channels;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

public sealed record NativeStatusTransportResult(
    bool Accepted,
    RuntimeSnapshot Snapshot,
    ImmutableArray<string> Diagnostics);

/// <summary>Bounded JSONL boundary from the bridge into the runtime owner.</summary>
public sealed class NativeStatusTransport
{
    private readonly RuntimeStateEngine _engine;

    public NativeStatusTransport(RuntimeStateEngine? engine = null) =>
        _engine = engine ?? new RuntimeStateEngine();

    public RuntimeSnapshot Snapshot => _engine.Snapshot;

    public NativeStatusTransportResult Accept(string json)
    {
        var parsed = NativeThreadStatusAdapter.Parse(json);
        if (!parsed.IsValid)
            return new(false, _engine.Snapshot, parsed.Diagnostics);

        var result = _engine.Apply(NativeThreadStatusAdapter.ToObservation(parsed.Event!));
        return new(true, result.Snapshot, result.Diagnostics);
    }
}

public sealed record NativeStatusDeliveryHealth(
    bool IsHealthy,
    int Queued,
    long Accepted,
    long Delivered,
    long Overflow,
    long SinkFailures);

/// <summary>
/// Serializes native authority records without blocking the bridge transport.
/// TryEnqueue fails closed at capacity and records degraded health explicitly.
/// </summary>
public sealed class NativeStatusDeliveryQueue : IAsyncDisposable
{
    private readonly NativeStatusTransport _transport;
    private readonly Channel<string> _channel;
    private readonly int _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _accepted;
    private long _delivered;
    private long _overflow;
    private long _sinkFailures;

    public NativeStatusDeliveryQueue(NativeStatusTransport transport, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _transport = transport;
        _capacity = capacity;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public bool TryEnqueue(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
        {
            Interlocked.Increment(ref _overflow);
            return false;
        }

        if (_channel.Reader.Count >= _capacity || !_channel.Writer.TryWrite(json))
        {
            Interlocked.Increment(ref _overflow);
            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    public NativeStatusDeliveryHealth Health => new(
        IsHealthy: Volatile.Read(ref _overflow) == 0 && Volatile.Read(ref _sinkFailures) == 0,
        Queued: _channel.Reader.Count,
        Accepted: Volatile.Read(ref _accepted),
        Delivered: Volatile.Read(ref _delivered),
        Overflow: Volatile.Read(ref _overflow),
        SinkFailures: Volatile.Read(ref _sinkFailures));

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var json in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var result = _transport.Accept(json);
                    if (result.Accepted) Interlocked.Increment(ref _delivered);
                    else Interlocked.Increment(ref _sinkFailures);
                }
                catch
                {
                    Interlocked.Increment(ref _sinkFailures);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _stop.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
