using System.Text;
using System.Text.Json;
using Vorotex.K15.StatusLab;

internal static class CodexReadAckTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
    private static int _passed;
    private static void Check(bool value, string id)
    {
        if (!value) throw new InvalidOperationException("READ_ACK_" + id);
        _passed++;
        Console.WriteLine("READ_ACK_" + id + "=PASS");
    }
    private static StatusInputEvent Hook(string name, int second, string session = "S", string thread = "T", string turn = "U") =>
        new(T.AddSeconds(second), "codex_hook", name, SessionId: session, ThreadId: thread, TurnId: turn);
    private static StateReducer Done(string session = "S", string thread = "T", string turn = "U")
    {
        var reducer = new StateReducer(0, T);
        reducer.Apply(Hook("UserPromptSubmit", 1, session, thread, turn));
        reducer.Apply(Hook("Stop", 2, session, thread, turn));
        return reducer;
    }
    private sealed class Reader : ICodexUnreadStateReader
    {
        public string[]? Ids = ["T"];
        public string Host = "local";
        public CodexUnreadState Failure = CodexUnreadState.Unknown;
        public int Calls;
        public int Offset;
        public CodexUnreadSnapshot Read(DateTimeOffset startedUtc)
        {
            Calls++;
            return new(Host, startedUtc.AddSeconds(Offset), startedUtc.AddSeconds(Offset).AddMilliseconds(10),
                Ids?.ToHashSet(StringComparer.Ordinal), Failure);
        }
    }
    private static CodexReadAckEvidence Evidence(StateReducer reducer, int second = 3) =>
        new(reducer.ReadAckCandidates.Single(), "local", T.AddSeconds(second), T.AddSeconds(second + 1), T.AddSeconds(second + 2));
    private static CodexUnreadState Parse(string json, string thread = "T") =>
        CodexUnreadStateReader.Parse(Encoding.UTF8.GetBytes(json), "local", T, T).ForThread(thread);
    private static string Store(string atom) => "{\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":" + atom + "},\"private\":\"must not escape\"}";

    public static void Run()
    {
        Check(Parse(Store("{\"local\":[\"T\"],\"remote\":[\"R\"]}")) == CodexUnreadState.HasUnread, "EXACT_HOST_THREAD");
        Check(Parse(Store("{\"local\":[],\"remote\":[\"T\"]}")) == CodexUnreadState.NoUnread, "HOST_ISOLATION");
        Check(Parse(Store("{\"remote\":[]}")) == CodexUnreadState.Unknown, "MISSING_HOST_UNKNOWN");
        Check(Parse(Store("{}")) == CodexUnreadState.Unknown, "LAST_HOST_DISAPPEARED_UNKNOWN");
        Check(Parse("{}") == CodexUnreadState.Unavailable, "MISSING_ATOM_UNAVAILABLE");
        foreach (var (id, json) in new[]
        {
            ("HOST_DUPLICATE", Store("{\"local\":[\"T\"],\"local\":[]}")),
            ("HOST_MALFORMED", Store("{\"local\":null}")),
            ("OTHER_HOST_MALFORMED", Store("{\"local\":[],\"remote\":[1]}")),
            ("ID_DUPLICATE", Store("{\"local\":[\"T\",\"T\"]}")),
            ("TRUNCATION", Store("{\"local\":[]}")[..^1]),
            ("TRAILING_GARBAGE", Store("{\"local\":[]}") + "garbage"),
            ("ROOT_DUPLICATE", "{\"electron-persisted-atom-state\":{},\"electron-persisted-atom-state\":{}}"),
            ("ATOM_DUPLICATE", "{\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":{},\"unread-thread-ids-by-host-v1\":{}}}"),
            ("ID_TOO_LONG", Store(JsonSerializer.Serialize(new { local = new[] { new string('x', 1025) } }))),
            ("ID_LIMIT", Store(JsonSerializer.Serialize(new { local = Enumerable.Range(0, 10001).Select(i => "id" + i) }))),
            ("HOST_LIMIT", Store(JsonSerializer.Serialize(Enumerable.Range(0, 257).ToDictionary(i => "h" + i, _ => Array.Empty<string>()))))
        }) Check(Parse(json) == CodexUnreadState.Unknown, id);
        Check(CodexUnreadStateReader.Parse(new byte[CodexUnreadStateReader.MaxBytes + 1], "local", T, T).Failure == CodexUnreadState.Unknown, "BYTE_LIMIT");
        Check(CodexUnreadStateReader.ResolveStatePath(null) is null && CodexUnreadStateReader.ResolveStatePath("relative") is null,
            "HOME_EXPLICIT_NO_STALE_FALLBACK");

        var reducer = Done();
        var reader = new Reader { Ids = [] };
        var observer = new CodexReadAckObserver(reader, "local");
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3)).Count == 0 &&
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4)).Count == 0, "INITIAL_NOUNREAD_NO_ACK");
        reader.Ids = ["T"];
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(5)).Count == 0, "HASUNREAD_ONLY_NO_ACK");
        reader.Ids = [];
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(6)).Count == 0, "FIRST_NOUNREAD_NO_ACK");
        var evidence = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(7)).Single();
        Check(reducer.ApplyReadAck(evidence)?.Reason == "codex_read_ack" && reducer.Snapshot.DoneUnreadCount == 0 &&
            reducer.LastSessionTransitions.Single().ThreadId == "T", "CAUSAL_ACK");
        Check(reducer.ApplyReadAck(evidence) is null && reducer.LastSessionTransitions.Count == 0, "DUPLICATE_ACK_IDEMPOTENT");
        reducer.Apply(Hook("Stop", 8));
        Check(reducer.State == K15NormalizedState.Normal, "EXACT_LATE_STOP_SUPPRESSED");
        reducer.Apply(Hook("Stop", 9, turn: "U2"));
        Check(reducer.State == K15NormalizedState.DonePendingAttention, "NEW_TURN_STOP_PRESERVED");

        reducer = Done();
        evidence = Evidence(reducer);
        reducer.Apply(Hook("UserPromptSubmit", 4, turn: "U2"));
        Check(reducer.ApplyReadAck(evidence) is null && reducer.State == K15NormalizedState.Running, "NEW_TURN_INVALIDATES_ARM");
        reducer.Apply(Hook("PermissionRequest", 5, turn: "U2"));
        Check(reducer.ApplyReadAck(evidence) is null && reducer.State == K15NormalizedState.Waiting, "WAITING_NEVER_READ_ACK");
        reducer = Done();
        evidence = Evidence(reducer);
        reducer.Apply(Hook("UserPromptSubmit", 6));
        reducer.Apply(Hook("Stop", 7));
        Check(reducer.ApplyReadAck(evidence) is null && reducer.State == K15NormalizedState.DonePendingAttention, "OLD_GENERATION_REJECTED");
        Check(reducer.ApplyReadAck(Evidence(reducer, 8) with { Host = "remote" }) is null, "WRONG_HOST_EVIDENCE_REJECTED");
        evidence = Evidence(reducer, 8);
        Check(reducer.ApplyReadAck(evidence with { HasUnreadUtc = T }) is null &&
            reducer.ApplyReadAck(evidence with { FirstNoUnreadUtc = evidence.HasUnreadUtc }) is null &&
            reducer.ApplyReadAck(evidence with { SecondNoUnreadUtc = evidence.FirstNoUnreadUtc }) is null, "OUT_OF_ORDER_REJECTED");

        reducer = Done();
        reducer.Apply(Hook("UserPromptSubmit", 1, "B", "TB", "UB"));
        reducer.Apply(Hook("Stop", 2, "B", "TB", "UB"));
        evidence = new(reducer.ReadAckCandidates.Single(k => k.SessionId == "S"), "local", T.AddSeconds(3), T.AddSeconds(4), T.AddSeconds(5));
        Check(reducer.ApplyReadAck(evidence) is null && reducer.LastSessionTransitions.Single().SessionId == "S" &&
            reducer.Snapshot.DoneUnreadCount == 1 && reducer.SessionSnapshots.Single(s => s.SessionId == "B").State == K15NormalizedState.DonePendingAttention,
            "A_ACK_B_DONE_AGGREGATE_RETAINS_DONE");
        reducer = Done();
        reducer.Apply(Hook("UserPromptSubmit", 1, "B", "T", "UB"));
        reducer.Apply(Hook("Stop", 2, "B", "T", "UB"));
        Check(reducer.ReadAckCandidates.Count == 0, "AMBIGUOUS_THREAD_FAIL_CLOSED");
        Check(Done(thread: "").ReadAckCandidates.Count == 0 && Done(turn: "").ReadAckCandidates.Count == 0,
            "NO_UNREAD_ID_EQUALS_SESSION_FALLBACK");
        Check(Done(thread: new string('x', 129)).ReadAckCandidates.Count == 0, "IDENTITIES_NEVER_TRUNCATED");
        reducer = Done();
        reducer.Apply(Hook("SessionEnd", 3));
        Check(reducer.ApplyReadAck(Evidence(reducer, 4))?.Reason == "codex_read_ack", "ENDED_DONE_EXACT_ACK");
        reducer = Done();
        reducer.Apply(Hook("PermissionRequest", 3, "B", "TB", "UB"));
        Check(reducer.ApplyReadAck(Evidence(reducer, 4)) is null && reducer.LastSessionTransitions.Single().SessionId == "S" &&
            reducer.State == K15NormalizedState.Waiting && reducer.SessionSnapshots.Single(s => s.SessionId == "B").State == K15NormalizedState.Waiting,
            "A_READ_ACK_PRESERVES_UNRELATED_WAITING");

        foreach (var failure in new[] { CodexUnreadState.Unknown, CodexUnreadState.Unavailable })
        {
            reducer = Done(); reader = new Reader(); observer = new(reader, "local");
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3));
            reader.Ids = null; reader.Failure = failure;
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4));
            reader.Ids = [];
            Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(5)).Count == 0 &&
                observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(6)).Count == 0, failure + "_BREAKS_CAUSAL_CHAIN");
        }
        reducer = Done(); reader = new Reader(); observer = new(reader, "local");
        observer.Poll([], T.AddSeconds(3));
        Check(reader.Calls == 0, "NO_CANDIDATES_NO_IO");
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4));
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4.5));
        Check(reader.Calls == 1, "BOUNDED_ONE_POLL_PER_SECOND");
        observer.Poll(Enumerable.Repeat(reducer.ReadAckCandidates.Single(), 257).ToArray(), T.AddSeconds(5));
        Check(reader.Calls == 1, "COMPLETION_OVERFLOW_NO_IO");
        reader.Offset = -5;
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(6)).Count == 0, "STALE_READ_REJECTED");
        reader.Offset = 0; reader.Host = "remote";
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(7)).Count == 0, "HOST_SWITCH_REJECTED");

        var replay = new[] { Hook("UserPromptSubmit", 1), Hook("Stop", 2) };
        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        reader = new Reader { Ids = [] }; observer = new(reader, "local");
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(11)).Count == 0 &&
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(12)).Count == 0 && reducer.Snapshot.DoneUnreadCount == 1,
            "REHYDRATION_INITIAL_NOUNREAD_PRESERVES_DONE");
        reader.Ids = ["T"]; observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(13));
        reader.Ids = []; observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(14));
        evidence = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(15)).Single();
        Check(reducer.ApplyReadAck(evidence)?.Reason == "codex_read_ack", "REHYDRATION_FRESH_CAUSAL_ACK");
        reducer.Rehydrate(replay);
        Check(reducer.ApplyReadAck(evidence) is null && reducer.Snapshot.DoneUnreadCount == 1, "PRIOR_RUNTIME_EPOCH_REJECTED");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(new[] { Hook("UserPromptSubmit", 1, thread: ""), Hook("Stop", 2, thread: "") });
        var persisted = new SessionStateTransition("S", K15NormalizedState.Running, K15NormalizedState.DonePendingAttention,
            "codex_stop", T.AddSeconds(2), "T", "U", "", "", false);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_MISSING_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted with { TimestampUtc = T }]);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_STALE_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted]);
        Check(reducer.ReadAckCandidates.Single().ThreadId == "T", "REHYDRATION_EXACT_PERSISTED_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted, persisted with { ThreadId = "OTHER" }]);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_AMBIGUOUS_CORRELATION");
        Check(JournalStateNormalizer.ParseInput("{\"source\":\"state_normalizer\",\"event\":\"read_ack_evidence\"}") is null,
            "ACK_DIAGNOSTIC_NEVER_REPLAYED_AS_INPUT");

        FileReaderTests();
        ArchitectRegressions();
        NormalizerTests();
        Console.WriteLine($"READ_ACK_SCENARIOS_PASSED={_passed}");
    }

    private static void FileReaderTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "k15-read-ack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        try
        {
            var reader = new CodexUnreadStateReader(path, "local");
            Check(reader.Read(DateTimeOffset.UtcNow).Failure == CodexUnreadState.Unavailable, "FILE_MISSING");
            var original = Encoding.UTF8.GetBytes(Store("{\"local\":[\"T\"]}"));
            File.WriteAllBytes(path, original);
            Check(reader.Read(DateTimeOffset.UtcNow).ForThread("T") == CodexUnreadState.HasUnread &&
                File.ReadAllBytes(path).SequenceEqual(original), "READ_ONLY_FILE_BYTES_UNCHANGED");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(reader.Read(DateTimeOffset.UtcNow).ThreadIds is null, "LOCKED_FILE_FAIL_CLOSED");
            var replacement = Path.Combine(directory, "replacement.json");
            File.WriteAllText(replacement, Store("{\"local\":[]}"));
            File.Move(replacement, path, true);
            Check(reader.Read(DateTimeOffset.UtcNow).ForThread("T") == CodexUnreadState.NoUnread, "ATOMIC_REPLACEMENT_REOPENED");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void NormalizerTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "k15-read-ack-normalizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        EventJournal.SetTestDirectoryPath(directory);
        try
        {
            EventJournal.SetDetailedLoggingEnabled(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var (session, thread) in new[] { ("S", "T"), ("B", "TB") })
                foreach (var (name, delta) in new[] { ("UserPromptSubmit", -4), ("Stop", -3) })
                    EventJournal.Append(new { timestampUtc = now.AddSeconds(delta), source = "codex_hook", @event = name,
                        sessionId = session, threadId = thread, turnId = "U" });
            var reader = new Reader { Ids = ["T", "TB"] };
            var normalizer = new JournalStateNormalizer(0, reader);
            try
            {
                normalizer.Start();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (reader.Calls < 1 && DateTime.UtcNow < deadline) Thread.Sleep(30);
                Check(reader.Calls >= 1 && normalizer.AttentionSnapshot.DoneUnreadCount == 2, "NORMALIZER_START_NO_ACK");
                reader.Ids = ["TB"];
                deadline = DateTime.UtcNow.AddSeconds(5);
                while (normalizer.AttentionSnapshot.DoneUnreadCount != 1 && DateTime.UtcNow < deadline) Thread.Sleep(30);
                Check(normalizer.AttentionSnapshot.DoneUnreadCount == 1 && normalizer.State == K15NormalizedState.DonePendingAttention &&
                    normalizer.SessionSnapshots.Single(s => s.SessionId == "S").State == K15NormalizedState.Normal,
                    "NORMALIZER_ACTUAL_POLL_A_ACK_B_RETAINED");
            }
            finally { normalizer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            var lines = File.ReadAllLines(EventJournal.FilePath);
            var receipt = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var evidence = receipt.Single(d => d.RootElement.GetProperty("event").GetString() == "read_ack_evidence").RootElement;
                Check(evidence.GetProperty("sessionId").GetString() == "S" &&
                    evidence.GetProperty("reason").GetString() == "codex_read_ack", "DETAILED_OFF_ACK_EVIDENCE");
                Check(receipt.Any(d => d.RootElement.GetProperty("event").GetString() == "session_state_changed" &&
                    d.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() == "codex_read_ack"),
                    "DETAILED_OFF_PER_SESSION_ACK");
                var bad = JsonSerializer.Deserialize<Dictionary<string, object>>(evidence.GetRawText())!;
                bad["prompt"] = "PRIVATE_SENTINEL";
                EventJournal.Append(bad);
                bad.Remove("prompt"); bad["completionGeneration"] = "PRIVATE_SENTINEL";
                EventJournal.Append(bad);
                Check(!File.ReadAllText(EventJournal.FilePath).Contains("PRIVATE_SENTINEL"), "EVIDENCE_PRIVACY_ALLOWLIST");
            }
            finally { foreach (var doc in receipt) doc.Dispose(); }
        }
        finally { EventJournal.SetTestDirectoryPath(null); Directory.Delete(directory, true); }
    }

    private static void ArchitectRegressions()
    {
        var reducer = Done(session: "T", thread: "");
        var complete = new StatusInputEvent(T.AddSeconds(3), "codex_stdio_bridge", "turn_completed", ThreadId: "T", TurnId: "U",
            SchemaVersion: "k15-codex-completion/v1", CompletionStatus: "completed");
        var before = reducer.Snapshot;
        var result = reducer.Apply(complete);
        var binding = reducer.ReadAckCandidates.Single();
        Check(binding.SessionId == "T" && binding.ThreadId == "T" && binding.TurnId == "U" && binding.CompletedUtc == T.AddSeconds(2),
            "STOP_THEN_COMPLETION_BINDS_READ_ACK_CANDIDATE");
        Check(result is null && reducer.LastSessionTransitions.Count == 0 && reducer.Snapshot == before,
            "STOP_THEN_COMPLETION_NO_DUPLICATE_DONE");
        reducer.Apply(complete);
        Check(reducer.ReadAckCandidates.Single() == binding, "STOP_THEN_COMPLETION_PRESERVES_GENERATION");
        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate([Hook("UserPromptSubmit", 1, "T", ""), Hook("Stop", 2, "T", ""), complete]);
        reducer.RestoreCompletionCorrelations([new("T", K15NormalizedState.Running, K15NormalizedState.DonePendingAttention,
            "codex_stop", T.AddSeconds(2), "", "U", "", "", false)]);
        Check(reducer.ReadAckCandidates.Single().ThreadId == "T" && reducer.ReadAckCandidates.Single().TurnId == "U" &&
            reducer.LastSessionTransitions.Count(s => s.Current == K15NormalizedState.DonePendingAttention) == 1,
            "STOP_THEN_COMPLETION_REHYDRATES_EXACT_BINDING");
        reducer = Done(session: "T", thread: "CONFLICT");
        reducer.Apply(complete);
        reducer.Apply(complete);
        Check(reducer.ReadAckCandidates.Count == 0 && reducer.State == K15NormalizedState.DonePendingAttention &&
            reducer.LastSessionTransitions.Count == 0, "STOP_THEN_CONFLICTING_COMPLETION_FAILS_CLOSED");

        reducer = Done(); var reader = new Reader(); var observer = new CodexReadAckObserver(reader, "local");
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3));
        reader.Ids = []; observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4));
        var ready = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(5)).Single();
        var pendingB = Hook("PreToolUse", 5, "B", "TB", "UB");
        Check(!JournalStateNormalizer.MayAffectCompletion(pendingB, ready.Completion) &&
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(6)).Single() == ready,
            "READY_ACK_SURVIVES_UNRELATED_REORDER_PENDING");
        reducer.Apply(pendingB);
        reducer.Apply(Hook("PreToolUse", 6, "B", "TB", "UB"));
        var delivered = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(7)).Single();
        Check(reducer.ApplyReadAck(delivered)?.Reason == "codex_read_ack" && reducer.State == K15NormalizedState.Running &&
            reducer.SessionSnapshots.Single(s => s.SessionId == "S").State == K15NormalizedState.Normal,
            "READY_ACK_A_APPLIES_WHILE_B_ACTIVITY_CONTINUES");
        Check(reducer.ApplyReadAck(delivered) is null && reducer.LastSessionTransitions.Count == 0 &&
            observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(8)).Count == 0, "READY_ACK_REPLAY_DOES_NOT_DOUBLE_ACK");
        reducer = Done(); reader = new Reader(); observer = new(reader, "local");
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3)); reader.Ids = [];
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4));
        ready = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(5)).Single();
        var pendingNewTurn = Hook("UserPromptSubmit", 6, turn: "U2");
        Check(JournalStateNormalizer.MayAffectCompletion(pendingNewTurn, ready.Completion), "READY_ACK_SAME_SESSION_PENDING_GUARD");
        reducer.Apply(pendingNewTurn);
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(7)).Count == 0 && reducer.ApplyReadAck(ready) is null &&
            reducer.State == K15NormalizedState.Running, "READY_ACK_REJECTED_AFTER_SAME_SESSION_NEW_TURN");
    }
}
