using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed class JournalStateNormalizer : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReorderDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StartupReplayWindow = TimeSpan.FromMinutes(30);
    private const int StartupReplayMaxLines = 5000;

    private readonly CancellationTokenSource _cts = new();
    private readonly StateReducer _reducer;
    private readonly List<StatusInputEvent> _pending = new();
    private Task? _loopTask;
    private long _readOffset;
    private string _tailRemainder = string.Empty;

    public JournalStateNormalizer(double doneAttentionTimeoutSeconds = 30)
    {
        _reducer = new StateReducer(doneAttentionTimeoutSeconds);
    }

    public event Action<K15NormalizedState, StateTransition?>? StateChanged;

    public K15NormalizedState State => _reducer.State;
    public string? FocusedSessionId => _reducer.FocusedSessionId;
    public string FocusedCwd => _reducer.FocusedCwd;

    public void Start()
    {
        if (_loopTask is not null)
            return;

        EventJournal.EnsureExists();
        var replayLines = SafeReadReplayLines();
        RehydrateFromRecentJournal(replayLines);
        _readOffset = SafeCurrentLength();

        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "state_normalizer",
            @event = "state_rehydrated",
            current = ToWireName(_reducer.State),
            focusedSessionId = _reducer.FocusedSessionId,
            focusedCwd = _reducer.FocusedCwd,
            activeTaskSessions = _reducer.ActiveTaskSessionCount,
            replayWindowMinutes = StartupReplayWindow.TotalMinutes
        });

        StateChanged?.Invoke(_reducer.State, null);
        _loopTask = Task.Run(ProcessLoopAsync);
    }

    public void Acknowledge()
    {
        var transition = _reducer.Acknowledge(DateTimeOffset.UtcNow);
        if (transition is not null)
            PublishTransition(transition);
    }

    private void RehydrateFromRecentJournal(string[] lines)
    {
        var cutoff = DateTimeOffset.UtcNow - StartupReplayWindow;
        var start = Math.Max(0, lines.Length - StartupReplayMaxLines);
        var events = new List<StatusInputEvent>();

        for (var index = start; index < lines.Length; index++)
        {
            var input = ParseInput(lines[index]);
            if (input is null ||
                !input.Source.Equals("codex_hook", StringComparison.Ordinal) ||
                input.TimestampUtc < cutoff)
            {
                continue;
            }

            events.Add(input);
        }

        _reducer.Rehydrate(events);
    }

    private async Task ProcessLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                CollectNewEvents();
                var nowUtc = DateTimeOffset.UtcNow;
                FlushReadyEvents(nowUtc - ReorderDelay);
                var timedTransition = _reducer.Tick(nowUtc);
                if (timedTransition is not null)
                    PublishTransition(timedTransition);
            }
            catch (Exception ex)
            {
                EventJournal.Append(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "state_normalizer",
                    @event = "normalizer_error",
                    exception = ex.GetType().FullName,
                    hresult = ex.HResult
                });
            }

            try
            {
                await Task.Delay(PollInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        FlushReadyEvents(DateTimeOffset.MaxValue);
    }

    private void CollectNewEvents()
    {
        EventJournal.EnsureExists();
        var length = SafeCurrentLength();
        if (length < _readOffset)
        {
            _readOffset = 0;
            _tailRemainder = string.Empty;
        }
        if (length == _readOffset)
            return;

        string appended;
        try
        {
            using var stream = new FileStream(EventJournal.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (_readOffset > stream.Length)
                _readOffset = 0;
            stream.Seek(_readOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096, leaveOpen: true);
            appended = reader.ReadToEnd();
            _readOffset = stream.Position;
        }
        catch
        {
            return;
        }

        if (appended.Length == 0)
            return;

        var combined = _tailRemainder + appended;
        var lines = combined.Split('\n');
        _tailRemainder = combined.EndsWith('\n') ? string.Empty : lines[^1];
        var count = combined.EndsWith('\n') ? lines.Length : lines.Length - 1;
        for (var index = 0; index < count; index++)
        {
            var input = ParseInput(lines[index].TrimEnd('\r'));
            if (input is not null)
                _pending.Add(input);
        }
    }

    private void FlushReadyEvents(DateTimeOffset thresholdUtc)
    {
        if (_pending.Count == 0)
            return;

        _pending.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
        var ready = _pending.TakeWhile(input => input.TimestampUtc <= thresholdUtc).ToArray();
        if (ready.Length == 0)
            return;
        _pending.RemoveRange(0, ready.Length);

        foreach (var input in ready)
        {
            var transition = _reducer.Apply(input);
            if (transition is not null)
                PublishTransition(transition);
        }
    }

    private void PublishTransition(StateTransition transition)
    {
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "state_normalizer",
            @event = "normalized_state_changed",
            previous = ToWireName(transition.Previous),
            current = ToWireName(transition.Current),
            reason = transition.Reason,
            focusedSessionId = _reducer.FocusedSessionId,
            focusedCwd = _reducer.FocusedCwd,
            sourceTimestampUtc = transition.TimestampUtc
        });
        StateChanged?.Invoke(transition.Current, transition);
    }

    private static StatusInputEvent? ParseInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var sourceNode) ||
                !root.TryGetProperty("event", out var eventNode) ||
                !root.TryGetProperty("timestampUtc", out var timestampNode) ||
                !DateTimeOffset.TryParse(timestampNode.GetString(), out var timestamp))
            {
                return null;
            }

            var source = sourceNode.GetString() ?? string.Empty;
            var eventName = eventNode.GetString() ?? string.Empty;
            if (source is not ("codex_hook" or "windows_notification"))
                return null;

            uint? notificationId = null;
            if (root.TryGetProperty("notificationId", out var notificationNode) && notificationNode.TryGetUInt32(out var id))
                notificationId = id;

            return new StatusInputEvent(
                timestamp.ToUniversalTime(),
                source,
                eventName,
                notificationId,
                root.TryGetProperty("packageFamilyName", out var packageNode) ? packageNode.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("errorHint", out var errorNode) && errorNode.ValueKind == JsonValueKind.True,
                root.TryGetProperty("sessionId", out var sessionNode) ? sessionNode.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("turnId", out var turnNode) ? turnNode.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("cwd", out var cwdNode) ? cwdNode.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static string[] SafeReadReplayLines()
    {
        try
        {
            return File.ReadLines(EventJournal.FilePath)
                .TakeLast(StartupReplayMaxLines)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static long SafeCurrentLength()
    {
        try { return new FileInfo(EventJournal.FilePath).Length; }
        catch { return 0; }
    }

    public static string ToWireName(K15NormalizedState state) => state switch
    {
        K15NormalizedState.Normal => "NORMAL",
        K15NormalizedState.Running => "RUNNING",
        K15NormalizedState.Waiting => "WAITING",
        K15NormalizedState.DonePendingAttention => "DONE_PENDING_ATTENTION",
        K15NormalizedState.Error => "ERROR",
        _ => state.ToString().ToUpperInvariant()
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
