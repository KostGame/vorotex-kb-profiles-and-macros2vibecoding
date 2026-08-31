using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed class JournalStateNormalizer : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReorderDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StartupReplayWindow = TimeSpan.FromMinutes(30);
    private const int StartupReplayMaxLines = 5000;
    private const string ApprovalSource = "codex_stdio_bridge";
    private const string ApprovalSchemaVersion = "k15-codex-approval/v1";
    private const string CompletionSchemaVersion = "k15-codex-completion/v1";

    private readonly CancellationTokenSource _cts = new();
    private readonly StateReducer _reducer;
    private readonly List<StatusInputEvent> _pending = new();
    private Task? _loopTask;
    private long _readOffset;
    private string _tailRemainder = string.Empty;

    public JournalStateNormalizer(double staleAttentionTimeoutSeconds = 18000)
    {
        _reducer = new StateReducer(staleAttentionTimeoutSeconds);
    }

    public event Action<K15NormalizedState, StateTransition?>? StateChanged;

    public K15NormalizedState State => _reducer.State;
    public string? FocusedSessionId => _reducer.FocusedSessionId;
    public string FocusedCwd => _reducer.FocusedCwd;
    public CodexAttentionSnapshot AttentionSnapshot => _reducer.Snapshot;
    public IReadOnlyList<CodexSessionSnapshot> SessionSnapshots => _reducer.SessionSnapshots;

    public void Start()
    {
        if (_loopTask is not null)
            return;

        EventJournal.EnsureExists();
        var replayLines = SafeReadReplayLines();
        var replayTransitions = RehydrateFromRecentJournal(replayLines);
        _readOffset = SafeCurrentLength();

        foreach (var transition in replayTransitions)
            PublishSessionTransition(transition);

        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "state_normalizer",
            @event = "state_rehydrated",
            current = ToWireName(_reducer.State),
            focusedSessionId = _reducer.FocusedSessionId,
            focusedCwd = _reducer.FocusedCwd,
            activeTaskSessions = _reducer.ActiveTaskSessionCount,
            attention = _reducer.Snapshot,
            replayWindowMinutes = StartupReplayWindow.TotalMinutes
        });

        StateChanged?.Invoke(_reducer.State, null);
        _loopTask = Task.Run(ProcessLoopAsync);
    }

    public void Acknowledge()
    {
        var transition = _reducer.Acknowledge(DateTimeOffset.UtcNow);
        foreach (var sessionTransition in _reducer.LastSessionTransitions)
            PublishSessionTransition(sessionTransition);
        if (transition is not null)
            PublishTransition(transition);
    }

    private IReadOnlyList<SessionStateTransition> RehydrateFromRecentJournal(string[] lines)
    {
        var cutoff = DateTimeOffset.UtcNow - StartupReplayWindow;
        var start = Math.Max(0, lines.Length - StartupReplayMaxLines);
        var events = new List<StatusInputEvent>();

        for (var index = start; index < lines.Length; index++)
        {
            var input = ParseInput(lines[index]);
            if (input is null ||
                !(input.Source.Equals("codex_hook", StringComparison.Ordinal) ||
                  input.Source.Equals(ApprovalSource, StringComparison.Ordinal)) ||
                input.TimestampUtc < cutoff)
            {
                continue;
            }

            events.Add(input);
        }

        _reducer.Rehydrate(events);
        return _reducer.LastSessionTransitions;
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
            _pending.Clear();
        }

        if (length <= _readOffset)
            return;

        byte[] delta;
        try
        {
            using var stream = new FileStream(
                EventJournal.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _readOffset)
            {
                _readOffset = 0;
                _tailRemainder = string.Empty;
            }

            stream.Seek(_readOffset, SeekOrigin.Begin);
            var remaining = checked((int)(stream.Length - _readOffset));
            delta = new byte[remaining];
            var total = 0;
            while (total < delta.Length)
            {
                var read = stream.Read(delta, total, delta.Length - total);
                if (read == 0)
                    break;
                total += read;
            }

            if (total != delta.Length)
                Array.Resize(ref delta, total);
            _readOffset = stream.Position;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (delta.Length == 0)
            return;

        var text = _tailRemainder + Encoding.UTF8.GetString(delta);
        var lines = text.Split('\n');
        var completeCount = lines.Length - 1;
        for (var index = 0; index < completeCount; index++)
        {
            var input = ParseInput(lines[index].TrimEnd('\r'));
            if (input is not null)
                _pending.Add(input);
        }

        _tailRemainder = text.EndsWith('\n') ? string.Empty : lines[^1];
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
            foreach (var sessionTransition in _reducer.LastSessionTransitions)
                PublishSessionTransition(sessionTransition);
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
            plane = "aggregate",
            previous = ToWireName(transition.Previous),
            current = ToWireName(transition.Current),
            reason = transition.Reason,
            sourceTimestampUtc = transition.TimestampUtc,
            focusedSessionId = _reducer.FocusedSessionId,
            focusedCwd = _reducer.FocusedCwd,
            activeTaskSessions = _reducer.ActiveTaskSessionCount,
            attention = _reducer.Snapshot,
            aggregatePrevious = ToWireName(transition.Previous),
            aggregateCurrent = ToWireName(transition.Current),
            driverSessionId = _reducer.Snapshot.DriverSessionId,
            driverReason = _reducer.Snapshot.DriverReason,
            runningCount = _reducer.Snapshot.RunningCount,
            waitingCount = _reducer.Snapshot.ApprovalWaitingCount,
            doneUnreadCount = _reducer.Snapshot.DoneUnreadCount
        });

        StateChanged?.Invoke(transition.Current, transition);
    }

    private void PublishSessionTransition(SessionStateTransition transition)
    {
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "state_normalizer",
            @event = "session_state_changed",
            plane = "per_session",
            sessionId = BoundOpaque(transition.SessionId),
            previous = ToWireName(transition.Previous),
            current = ToWireName(transition.Current),
            reason = transition.Reason,
            sourceTimestampUtc = transition.TimestampUtc,
            isRehydrated = transition.IsRehydrated,
            correlation = new
            {
                threadId = BoundOpaque(transition.ThreadId),
                turnId = BoundOpaque(transition.TurnId),
                rpcIdType = transition.RpcIdType,
                rpcId = BoundOpaque(transition.RpcId)
            }
        });
    }

    private static string BoundOpaque(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 128 ? value : value[..128];

    internal static StatusInputEvent? ParseInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var source = GetString(root, "source");
            if (source == ApprovalSource)
                return ParseBridgeInput(root);

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
                GetString(root, "cwd"),
                ThreadId: GetString(root, "threadId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StatusInputEvent? ParseApprovalInput(JsonElement root)
    {
        const string eventName = "approval_resolved";
        var schemaVersion = GetBoundedString(root, "schemaVersion");
        var parsedEvent = GetBoundedString(root, "event");
        var decision = GetBoundedString(root, "decision");
        var rpcIdType = GetBoundedString(root, "rpcIdType");
        var rpcId = GetBoundedString(root, "rpcId");
        var timestampText = GetBoundedString(root, "timestampUtc");
        if (root.EnumerateObject().Any(property =>
                property.Value.ValueKind != JsonValueKind.String ||
                Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty) > 1024))
        {
            return null;
        }

        if (schemaVersion != ApprovalSchemaVersion ||
            parsedEvent != eventName ||
            decision is not ("accept" or "acceptForSession" or "decline" or "cancel") ||
            rpcIdType is not ("number" or "string") ||
            string.IsNullOrWhiteSpace(rpcId) ||
            !DateTimeOffset.TryParse(timestampText, out var timestampUtc))
        {
            return null;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "timestampUtc", "source", "event", "decision",
            "rpcIdType", "rpcId", "threadId", "turnId", "itemId"
        };
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return null;

        return new StatusInputEvent(
            timestampUtc.ToUniversalTime(),
            ApprovalSource,
            eventName,
            SchemaVersion: schemaVersion,
            Decision: decision,
            RpcIdType: rpcIdType,
            RpcId: rpcId,
            ThreadId: GetBoundedString(root, "threadId"),
            TurnId: GetBoundedString(root, "turnId"),
            ItemId: GetBoundedString(root, "itemId"));
    }

    private static StatusInputEvent? ParseBridgeInput(JsonElement root)
    {
        var schemaVersion = GetBoundedString(root, "schemaVersion");
        var eventName = GetBoundedString(root, "event");
        var timestampText = GetBoundedString(root, "timestampUtc");
        if (schemaVersion == CompletionSchemaVersion && eventName == "turn_completed")
        {
            var status = GetBoundedString(root, "status");
            if (status is not ("completed" or "interrupted" or "failed") ||
                !DateTimeOffset.TryParse(timestampText, out var timestampUtc) ||
                root.EnumerateObject().Any(property => property.Name is not (
                    "schemaVersion" or "timestampUtc" or "source" or "event" or
                    "threadId" or "turnId" or "status") ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    Encoding.UTF8.GetByteCount(property.Value.GetString() ?? string.Empty) > 1024))
                return null;

            var threadId = GetBoundedString(root, "threadId");
            var turnId = GetBoundedString(root, "turnId");
            return string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)
                ? null
                : new StatusInputEvent(timestampUtc.ToUniversalTime(), ApprovalSource, eventName,
                    SchemaVersion: schemaVersion, ThreadId: threadId, TurnId: turnId, CompletionStatus: status);
        }

        return ParseApprovalInput(root);
    }

    private static string[] SafeReadReplayLines()
    {
        var lines = new List<string>();
        foreach (var path in new[] { EventJournal.ArchivePath(1), EventJournal.FilePath })
        {
            try
            {
                if (File.Exists(path))
                    lines.AddRange(File.ReadLines(path));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return lines.Count <= StartupReplayMaxLines
            ? lines.ToArray()
            : lines.Skip(lines.Count - StartupReplayMaxLines).ToArray();
    }

    private static long SafeCurrentLength()
    {
        try
        {
            return File.Exists(EventJournal.FilePath) ? new FileInfo(EventJournal.FilePath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }

    private static string GetBoundedString(JsonElement root, string name)
    {
        const int maxBytes = 1024;
        var value = GetString(root, name);
        return value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= maxBytes ? value : string.Empty;
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
