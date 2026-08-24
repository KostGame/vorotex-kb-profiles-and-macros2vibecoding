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
    private int _processedLineCount;

    public JournalStateNormalizer(double doneAttentionTimeoutSeconds = 0)
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
        var lines = SafeReadAllLines();
        RehydrateFromRecentJournal(lines);
        _processedLineCount = lines.Length;

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
        var lines = SafeReadAllLines();
        if (lines.Length < _processedLineCount)
        {
            _processedLineCount = 0;
            _pending.Clear();
        }

        for (var index = _processedLineCount; index < lines.Length; index++)
        {
            var input = ParseInput(lines[index]);
            if (input is not null)
                _pending.Add(input);
        }

        _processedLineCount = lines.Length;
    }

    private void FlushReadyEvents(DateTimeOffset watermark)
    {
        if (_pending.Count == 0)
            return;

        _pending.Sort(static (left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));

        var readyCount = 0;
        while (readyCount < _pending.Count && _pending[readyCount].TimestampUtc <= watermark)
            readyCount++;

        for (var index = 0; index < readyCount; index++)
        {
            var transition = _reducer.Apply(_pending[index]);
            if (transition is not null)
                PublishTransition(transition);
        }

        if (readyCount > 0)
            _pending.RemoveRange(0, readyCount);
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
            sourceTimestampUtc = transition.TimestampUtc,
            focusedSessionId = _reducer.FocusedSessionId,
            focusedCwd = _reducer.FocusedCwd,
            activeTaskSessions = _reducer.ActiveTaskSessionCount
        });

        StateChanged?.Invoke(transition.Current, transition);
    }

    internal static StatusInputEvent? ParseInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var source = GetString(root, "source");
            if (source is not ("codex_hook" or "windows_notification"))
                return null;

            var eventName = GetString(root, "event");
            var timestampText = GetString(root, "timestampUtc");
            if (string.IsNullOrWhiteSpace(eventName) ||
                !DateTimeOffset.TryParse(timestampText, out var timestampUtc))
                return null;

            uint? notificationId = null;
            if (root.TryGetProperty("notificationId", out var idNode) &&
                idNode.ValueKind == JsonValueKind.Number &&
                idNode.TryGetUInt32(out var parsedId))
            {
                notificationId = parsedId;
            }

            var packageFamilyName = GetString(root, "packageFamilyName");
            var errorHint = root.TryGetProperty("errorHint", out var errorNode) &&
                errorNode.ValueKind is JsonValueKind.True;

            return new StatusInputEvent(
                timestampUtc.ToUniversalTime(),
                source,
                eventName,
                notificationId,
                packageFamilyName,
                errorHint,
                GetString(root, "sessionId"),
                GetString(root, "turnId"),
                GetString(root, "cwd"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] SafeReadAllLines()
    {
        try
        {
            return File.ReadAllLines(EventJournal.FilePath);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }

    internal static string ToWireName(K15NormalizedState state) => state switch
    {
        K15NormalizedState.Normal => "NORMAL",
        K15NormalizedState.Running => "RUNNING",
        K15NormalizedState.Waiting => "WAITING",
        K15NormalizedState.DonePendingAttention => "DONE_PENDING_ATTENTION",
        K15NormalizedState.Error => "ERROR",
        _ => "UNKNOWN"
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
