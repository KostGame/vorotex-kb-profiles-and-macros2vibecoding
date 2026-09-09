using System.Collections.Immutable;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

public sealed record NativeStatusTransportResult(
    bool Accepted,
    RuntimeSnapshot Snapshot,
    ImmutableArray<string> Diagnostics);

/// <summary>Bounded JSONL boundary from the bridge into the runtime owner.</summary>
public sealed class NativeStatusTransport
{
    private const int MaxResyncThreads = 128;
    private readonly RuntimeStateEngine _engine;
    private readonly HashSet<string> _staleNonterminalThreads = new(StringComparer.Ordinal);
    private RuntimeHealthSnapshot _health;
    private bool _resyncRequired;
    private bool _resyncOverflow;

    public NativeStatusTransport(RuntimeStateEngine? engine = null)
    {
        _engine = engine ?? new RuntimeStateEngine();
        _health = RuntimeHealthSnapshot.Healthy(_engine.Snapshot.Health.RuntimeVersion);
    }

    public RuntimeSnapshot Snapshot => _engine.Snapshot with { Health = _health };

    public void MarkDegraded(string reason)
    {
        var boundedReason = reason switch
        {
            "NATIVE_AUTHORITY_DEGRADED_OVERFLOW" => reason,
            "NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE" => reason,
            "NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE" => reason,
            "NATIVE_AUTHORITY_DEGRADED_RESYNC_REQUIRED" => reason,
            _ => "NATIVE_AUTHORITY_DEGRADED_UNKNOWN"
        };

        if (boundedReason == "NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE" && _health.Detail != boundedReason)
        {
            _staleNonterminalThreads.Clear();
            foreach (var thread in _engine.Snapshot.Threads.Where(thread => thread.RuntimeStatus == ThreadRuntimeStatus.Active))
            {
                if (_staleNonterminalThreads.Count == MaxResyncThreads)
                {
                    _resyncOverflow = true;
                    break;
                }
                _staleNonterminalThreads.Add(thread.ThreadId);
            }
            _resyncRequired = _staleNonterminalThreads.Count != 0 || _resyncOverflow;
        }

        _health = RuntimeHealthSnapshot.Degraded(_engine.Snapshot.Health.RuntimeVersion,
            _resyncRequired ? "NATIVE_AUTHORITY_DEGRADED_RESYNC_REQUIRED" : boundedReason);
    }

    public void MarkAvailable(string? refreshedThreadId = null)
    {
        if (_resyncRequired && refreshedThreadId is not null)
            _staleNonterminalThreads.Remove(refreshedThreadId);

        if (_resyncRequired && (_resyncOverflow || _staleNonterminalThreads.Count != 0))
            return;

        _resyncRequired = false;
        _resyncOverflow = false;
        if (_health.Detail is "NATIVE_AUTHORITY_DEGRADED_UNAVAILABLE" or "NATIVE_AUTHORITY_DEGRADED_RESYNC_REQUIRED")
            _health = RuntimeHealthSnapshot.Healthy(_engine.Snapshot.Health.RuntimeVersion);
    }

    public NativeStatusTransportResult AcceptAuthorityRecord(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > RuntimeIpcMetadata.MaxFrameBytes)
            return new(false, Snapshot, ImmutableArray.Create("NATIVE_AUTHORITY_FRAME_TOO_LARGE"));

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != System.Text.Json.JsonValueKind.String)
                return new(false, Snapshot, ImmutableArray.Create("INVALID_NATIVE_AUTHORITY_SCHEMA"));

            return schema.GetString() switch
            {
                RuntimeIpcMetadata.NativeAuthorityMetadataSchema => AcceptMetadata(json),
                RuntimeIpcMetadata.NativeAuthorityStatusSchema or RuntimeIpcMetadata.NativeAuthorityHealthSchema => Accept(json),
                _ => new(false, Snapshot, ImmutableArray.Create("UNKNOWN_NATIVE_AUTHORITY_SCHEMA")),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new(false, Snapshot, ImmutableArray.Create("MALFORMED_NATIVE_AUTHORITY_RECORD"));
        }
    }

    public NativeStatusTransportResult Accept(string json)
    {
        if (NativeAuthorityHealthAdapter.TryParse(json, out var degradedReason))
        {
            MarkDegraded(degradedReason);
            return new(true, Snapshot, ImmutableArray<string>.Empty);
        }

        var parsed = NativeThreadStatusAdapter.Parse(json);
        if (!parsed.IsValid)
            return new(false, Snapshot, parsed.Diagnostics);

        var result = _engine.Apply(NativeThreadStatusAdapter.ToObservation(parsed.Event!));
        if (!result.Diagnostics.Contains("STALE_NATIVE_RUNTIME_OBSERVATION_IGNORED")
            && !result.Diagnostics.Contains("DUPLICATE_NATIVE_RUNTIME_OBSERVATION_IGNORED"))
            MarkAvailable(parsed.Event!.ThreadId);
        return new(true, result.Snapshot with { Health = _health }, result.Diagnostics);
    }

    public NativeStatusTransportResult AcceptMetadata(string json)
    {
        var parsed = NativeThreadMetadataAdapter.Parse(json);
        if (!parsed.IsValid) return new(false, Snapshot, parsed.Diagnostics);
        var result = _engine.Apply(new ThreadMetadataObservation(parsed.Event!.ThreadId, parsed.Event.WorkingDirectory));
        return new(true, result.Snapshot with { Health = _health }, result.Diagnostics);
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
    private readonly Func<long, string, NativeStatusTransportResult> _apply;
    private readonly Action<string> _markDegraded;
    private readonly Queue<QueuedRecord> _queue = new();
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Task _worker;
    private long _accepted;
    private long _delivered;
    private long _overflow;
    private long _sinkFailures;

    public NativeStatusDeliveryQueue(NativeStatusTransport transport, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _apply = (_, json) => transport.AcceptAuthorityRecord(json);
        _markDegraded = transport.MarkDegraded;
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _worker = Task.Run(ConsumeAsync);
    }

    public NativeStatusDeliveryQueue(
        Func<string, NativeStatusTransportResult> apply,
        Action<string> markDegraded,
        int capacity = 64)
        : this((_, json) => apply(json), markDegraded, capacity)
    {
    }

    public NativeStatusDeliveryQueue(
        Func<long, string, NativeStatusTransportResult> apply,
        Action<string> markDegraded,
        int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(markDegraded);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _apply = apply;
        _markDegraded = markDegraded;
        _capacity = capacity;
        _worker = Task.Run(ConsumeAsync);
    }

    public bool TryEnqueue(string json)
        => TryEnqueue(0, json);

    public bool TryEnqueue(long producerGeneration, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
        {
            Overflow();
            return false;
        }

        lock (_gate)
        {
            if (_queue.Count >= _capacity)
            {
                Overflow();
                return false;
            }
            _queue.Enqueue(new QueuedRecord(producerGeneration, json));
        }

        Interlocked.Increment(ref _accepted);
        _signal.Release();
        return true;
    }

    public NativeStatusDeliveryHealth Health => new(
        IsHealthy: Volatile.Read(ref _overflow) == 0 && Volatile.Read(ref _sinkFailures) == 0,
        Queued: QueueCount(),
        Accepted: Volatile.Read(ref _accepted),
        Delivered: Volatile.Read(ref _delivered),
        Overflow: Volatile.Read(ref _overflow),
        SinkFailures: Volatile.Read(ref _sinkFailures));

    private async Task ConsumeAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                QueuedRecord record;
                lock (_gate)
                {
                    if (_queue.Count == 0) continue;
                    record = _queue.Dequeue();
                }
                try
                {
                    var result = _apply(record.ProducerGeneration, record.Json);
                    if (result.Accepted) Interlocked.Increment(ref _delivered);
                    else SinkFailure();
                }
                catch
                {
                    SinkFailure();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _signal.Release();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _signal.Dispose();
        _stop.Dispose();
    }

    private int QueueCount() { lock (_gate) return _queue.Count; }
    private void Overflow() { Interlocked.Increment(ref _overflow); _markDegraded("NATIVE_AUTHORITY_DEGRADED_OVERFLOW"); }
    private void SinkFailure() { Interlocked.Increment(ref _sinkFailures); _markDegraded("NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE"); }

    private sealed record QueuedRecord(long ProducerGeneration, string Json);
}
