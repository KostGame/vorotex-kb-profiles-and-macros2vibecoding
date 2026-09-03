using Vorotex.K15.StatusLab;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StatusInputEvent Hook(DateTimeOffset t, string name, string session = "session-main", string cwd = @"C:\work\main", string turn = "", string thread = "") =>
    new(t, "codex_hook", name, SessionId: session, TurnId: turn, Cwd: cwd, ThreadId: thread);
static StatusInputEvent Notification(DateTimeOffset t, string name, uint id, bool error = false) =>
    new(t, "windows_notification", name, id, "OpenAI.Codex_test", error);
static StatusInputEvent Approval(DateTimeOffset t, string decision, string rpcId = "1", string threadId = "", string turnId = "") =>
    new(t, "codex_stdio_bridge", "approval_resolved", SchemaVersion: "k15-codex-approval/v1",
        Decision: decision, RpcIdType: "number", RpcId: rpcId, ThreadId: threadId, TurnId: turnId, ItemId: "item-1");
static StatusInputEvent Completion(DateTimeOffset t, string threadId, string turnId, string status = "completed") =>
    new(t, "codex_stdio_bridge", "turn_completed", SchemaVersion: "k15-codex-completion/v1",
        CompletionStatus: status, ThreadId: threadId, TurnId: turnId);

var t = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
var reducer = new StateReducer();
Require(reducer.State == K15NormalizedState.Normal, "Initial state must be NORMAL.");
reducer.Apply(Hook(t, "UserPromptSubmit"));
Require(reducer.State == K15NormalizedState.Running, "UserPromptSubmit must enter RUNNING.");
Require(reducer.FocusedSessionId == "session-main", "Main task session must become focused.");
var noStop = new StateReducer();
noStop.Apply(Hook(t, "UserPromptSubmit", "session-done", turn: "turn-done", thread: "thread-done"));
var completion = noStop.Apply(Completion(t.AddSeconds(1), "", "turn-done"));
Require(completion is null && noStop.State == K15NormalizedState.Running,
    "Completion without exact thread correlation must not infer DONE.");
completion = noStop.Apply(Completion(t.AddSeconds(2), "thread-done", "turn-done"));
Require(completion?.Reason == "codex_turn_completed" && noStop.State == K15NormalizedState.DonePendingAttention &&
        noStop.LastSessionTransitions.Count == 1 && !noStop.LastSessionTransitions[0].IsRehydrated,
    "Matching turn/completed must produce one live per-session DONE transition.");
Console.WriteLine("EXACT_THREAD_ID_COMPLETION_STILL_PASS=PASS");
var completionDuplicate = noStop.Apply(Completion(t.AddSeconds(2.5), "thread-done", "turn-done"));
Require(completionDuplicate is null && noStop.LastSessionTransitions.Count == 0 &&
        noStop.State == K15NormalizedState.DonePendingAttention,
    "Duplicate completion after DONE must be idempotent.");

var fallback = new StateReducer();
fallback.Apply(Hook(t, "UserPromptSubmit", "fallback-session", turn: "fallback-turn"));
var fallbackTransition = fallback.Apply(Completion(t.AddSeconds(1), "fallback-session", "fallback-turn"));
Require(fallbackTransition?.Reason == "codex_turn_completed" && fallback.State == K15NormalizedState.DonePendingAttention &&
        fallback.LastSessionTransitions.Count == 1 && fallback.LastSessionTransitions[0].SessionId == "fallback-session",
    "Session ID fallback must complete a hook-created RUNNING session exactly once.");
Console.WriteLine("SESSION_ID_FALLBACK_COMPLETION=PASS");

var fallbackWrongTurn = new StateReducer();
fallbackWrongTurn.Apply(Hook(t, "UserPromptSubmit", "fallback-wrong-turn", turn: "fallback-turn"));
Require(fallbackWrongTurn.Apply(Completion(t.AddSeconds(1), "fallback-wrong-turn", "other-turn")) is null &&
        fallbackWrongTurn.State == K15NormalizedState.Running && fallbackWrongTurn.LastSessionTransitions.Count == 0,
    "Session ID fallback must require an exact ordinal turn match.");
Console.WriteLine("SESSION_ID_FALLBACK_WRONG_TURN_BLOCKED=PASS");

var fallbackThenStop = new StateReducer();
fallbackThenStop.Apply(Hook(t, "UserPromptSubmit", "fallback-stop", turn: "fallback-stop-turn"));
fallbackThenStop.Apply(Completion(t.AddSeconds(1), "fallback-stop", "fallback-stop-turn"));
fallbackThenStop.Apply(Hook(t.AddSeconds(2), "Stop", "fallback-stop", turn: "fallback-stop-turn"));
Require(fallbackThenStop.LastSessionTransitions.Count == 0 && fallbackThenStop.State == K15NormalizedState.DonePendingAttention,
    "Fallback completion followed by Stop must be idempotent.");
Console.WriteLine("SESSION_ID_FALLBACK_THEN_STOP_IDEMPOTENT=PASS");

var stopThenFallback = new StateReducer();
stopThenFallback.Apply(Hook(t, "UserPromptSubmit", "stop-fallback", turn: "stop-fallback-turn"));
stopThenFallback.Apply(Hook(t.AddSeconds(1), "Stop", "stop-fallback", turn: "stop-fallback-turn"));
stopThenFallback.Apply(Completion(t.AddSeconds(2), "stop-fallback", "stop-fallback-turn"));
Require(stopThenFallback.LastSessionTransitions.Count == 0 && stopThenFallback.State == K15NormalizedState.DonePendingAttention,
    "Stop followed by fallback completion must be idempotent.");
Console.WriteLine("STOP_THEN_SESSION_ID_FALLBACK_IDEMPOTENT=PASS");

var fallbackAfterAck = new StateReducer();
fallbackAfterAck.Apply(Hook(t, "UserPromptSubmit", "fallback-ack", turn: "fallback-ack-turn"));
fallbackAfterAck.Apply(Completion(t.AddSeconds(1), "fallback-ack", "fallback-ack-turn"));
fallbackAfterAck.Acknowledge(t.AddSeconds(1));
Require(fallbackAfterAck.Apply(Completion(t.AddSeconds(2), "fallback-ack", "fallback-ack-turn")) is null &&
        fallbackAfterAck.State == K15NormalizedState.Normal && fallbackAfterAck.LastSessionTransitions.Count == 0,
    "Late fallback completion must not resurrect an acknowledged session.");
Console.WriteLine("SESSION_ID_FALLBACK_AFTER_ACK_BLOCKED=PASS");

var fallbackWaiting = new StateReducer();
fallbackWaiting.Apply(Hook(t, "UserPromptSubmit", "fallback-waiting", turn: "fallback-waiting-turn"));
fallbackWaiting.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "fallback-waiting", turn: "fallback-waiting-turn"));
Require(fallbackWaiting.Apply(Completion(t.AddSeconds(2), "fallback-waiting", "fallback-waiting-turn")) is null &&
        fallbackWaiting.State == K15NormalizedState.Waiting && fallbackWaiting.LastSessionTransitions.Count == 0,
    "Fallback completion must not auto-complete WAITING.");
Console.WriteLine("SESSION_ID_FALLBACK_WAITING_BLOCKED=PASS");

var fallbackEnded = new StateReducer();
fallbackEnded.Apply(Hook(t, "UserPromptSubmit", "fallback-ended", turn: "fallback-ended-turn"));
fallbackEnded.Apply(Hook(t.AddSeconds(1), "SessionEnd", "fallback-ended", turn: "fallback-ended-turn"));
Require(fallbackEnded.Apply(Completion(t.AddSeconds(2), "fallback-ended", "fallback-ended-turn")) is null &&
        fallbackEnded.State == K15NormalizedState.Normal && fallbackEnded.LastSessionTransitions.Count == 0,
    "Fallback completion must not resurrect an ended session.");
Console.WriteLine("SESSION_ID_FALLBACK_ENDED_BLOCKED=PASS");

var fallbackParallel = new StateReducer();
fallbackParallel.Apply(Hook(t, "UserPromptSubmit", "fallback-parallel-A", turn: "fallback-parallel-turn-A"));
fallbackParallel.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "fallback-parallel-B", turn: "fallback-parallel-turn-B"));
fallbackParallel.Apply(Completion(t.AddSeconds(2), "fallback-parallel-A", "fallback-parallel-turn-A"));
Require(fallbackParallel.SessionSnapshots.Single(s => s.SessionId == "fallback-parallel-A").State == K15NormalizedState.DonePendingAttention &&
        fallbackParallel.SessionSnapshots.Single(s => s.SessionId == "fallback-parallel-B").State == K15NormalizedState.Running &&
        fallbackParallel.LastSessionTransitions.Count == 1 && fallbackParallel.LastSessionTransitions[0].SessionId == "fallback-parallel-A",
    "Fallback completion must isolate the matching parallel session.");
Console.WriteLine("SESSION_ID_FALLBACK_PARALLEL_ISOLATION=PASS");

var fallbackAmbiguous = new StateReducer();
fallbackAmbiguous.Apply(Hook(t, "UserPromptSubmit", "fallback-ambiguous-A", turn: "fallback-ambiguous-turn"));
fallbackAmbiguous.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "fallback-ambiguous-B", turn: "fallback-ambiguous-turn", thread: "fallback-ambiguous-A"));
Require(fallbackAmbiguous.Apply(Completion(t.AddSeconds(2), "fallback-ambiguous-A", "fallback-ambiguous-turn")) is null &&
        fallbackAmbiguous.SessionSnapshots.All(s => s.State == K15NormalizedState.Running) &&
        fallbackAmbiguous.LastSessionTransitions.Count == 0,
    "Ambiguous fallback completion must fail closed without mutation.");
Console.WriteLine("SESSION_ID_FALLBACK_AMBIGUOUS_FAIL_CLOSED=PASS");
noStop.Acknowledge("session-done", t.AddSeconds(3));
Require(noStop.State == K15NormalizedState.Normal, "Explicit session ACK must clear DONE to NORMAL.");
var lateCompletion = noStop.Apply(Completion(t.AddSeconds(4), "thread-done", "turn-done"));
Require(lateCompletion is null && noStop.State == K15NormalizedState.Normal &&
        noStop.LastSessionTransitions.Count == 0,
    "Late completion after explicit ACK must not resurrect NORMAL.");
noStop.Apply(Hook(t.AddSeconds(3), "SessionEnd", "session-done", turn: "turn-done"));
Require(noStop.State == K15NormalizedState.Normal,
    "Ended explicitly acknowledged session must remain NORMAL.");
var idempotentStop = new StateReducer();
idempotentStop.Apply(Hook(t, "UserPromptSubmit", "idempotent-a", turn: "turn-a", thread: "thread-a"));
idempotentStop.Apply(Completion(t.AddSeconds(1), "thread-a", "turn-a"));
var beforeStop = idempotentStop.LastSessionTransitions.Count;
idempotentStop.Apply(Hook(t.AddSeconds(2), "Stop", "idempotent-a", turn: "turn-a", thread: "thread-a"));
Require(idempotentStop.LastSessionTransitions.Count == 0 && beforeStop == 1,
    "Completion then real Stop must not emit a second DONE transition.");
var idempotentCompletion = new StateReducer();
idempotentCompletion.Apply(Hook(t, "UserPromptSubmit", "idempotent-b", turn: "turn-b", thread: "thread-b"));
idempotentCompletion.Apply(Hook(t.AddSeconds(1), "Stop", "idempotent-b", turn: "turn-b", thread: "thread-b"));
idempotentCompletion.Apply(Completion(t.AddSeconds(2), "thread-b", "turn-b"));
Require(idempotentCompletion.LastSessionTransitions.Count == 0 &&
        idempotentCompletion.State == K15NormalizedState.DonePendingAttention,
    "Real Stop then completion must not emit a second DONE transition.");
var wrongTurn = new StateReducer();
wrongTurn.Apply(Hook(t, "UserPromptSubmit", "wrong-turn", turn: "turn-right", thread: "thread-same"));
Require(wrongTurn.Apply(Completion(t.AddSeconds(1), "thread-same", "turn-wrong")) is null &&
        wrongTurn.State == K15NormalizedState.Running && wrongTurn.LastSessionTransitions.Count == 0,
    "Same thread with wrong turn must not complete a session.");
var statusSemantics = new StateReducer();
statusSemantics.Apply(Hook(t, "UserPromptSubmit", "status-session", turn: "turn-status", thread: "thread-status"));
foreach (var status in new[] { "interrupted", "failed", "inProgress" })
{
    Require(statusSemantics.Apply(Completion(t.AddSeconds(1), "thread-status", "turn-status", status)) is null &&
            statusSemantics.State == K15NormalizedState.Running && statusSemantics.LastSessionTransitions.Count == 0,
        $"Completion status {status} must not produce DONE.");
}
var crossSession = new StateReducer();
crossSession.Apply(Hook(t, "UserPromptSubmit", "session-A", turn: "turn-A", thread: "thread-A"));
crossSession.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "session-B", turn: "turn-B", thread: "thread-B"));
crossSession.Apply(Completion(t.AddSeconds(2), "thread-A", "turn-A"));
Require(crossSession.SessionSnapshots.Single(s => s.SessionId == "session-A").State == K15NormalizedState.DonePendingAttention &&
        crossSession.SessionSnapshots.Single(s => s.SessionId == "session-B").State == K15NormalizedState.Running &&
        crossSession.LastSessionTransitions.Count == 1 && crossSession.LastSessionTransitions[0].SessionId == "session-A",
    "Completion must mutate only the exact matching session.");
var noMatch = new StateReducer();
noMatch.Apply(Hook(t, "UserPromptSubmit", "no-match", turn: "turn-no-match", thread: "thread-no-match"));
Require(noMatch.Apply(Completion(t.AddSeconds(1), "thread-absent", "turn-no-match")) is null &&
        noMatch.State == K15NormalizedState.Running && noMatch.LastSessionTransitions.Count == 0,
    "No matching completion candidate must fail closed.");
var ambiguous = new StateReducer();
ambiguous.Apply(Hook(t, "UserPromptSubmit", "ambiguous-A", turn: "turn-ambiguous", thread: "thread-ambiguous"));
ambiguous.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "ambiguous-B", turn: "turn-ambiguous", thread: "thread-ambiguous"));
Require(ambiguous.Apply(Completion(t.AddSeconds(2), "thread-ambiguous", "turn-ambiguous")) is null &&
        ambiguous.SessionSnapshots.All(s => s.State == K15NormalizedState.Running) &&
        ambiguous.LastSessionTransitions.Count == 0,
    "Multiple matching sessions must fail closed.");
var parallelPriority = new StateReducer();
parallelPriority.Apply(Hook(t, "UserPromptSubmit", "parallel-A", turn: "turn-parallel-A", thread: "thread-parallel-A"));
parallelPriority.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "parallel-B", turn: "turn-parallel-B", thread: "thread-parallel-B"));
var parallelTransition = parallelPriority.Apply(Completion(t.AddSeconds(2), "thread-parallel-A", "turn-parallel-A"));
Require(parallelTransition is null && parallelPriority.State == K15NormalizedState.Waiting &&
        parallelPriority.SessionSnapshots.Single(s => s.SessionId == "parallel-A").State == K15NormalizedState.DonePendingAttention &&
        parallelPriority.SessionSnapshots.Single(s => s.SessionId == "parallel-B").State == K15NormalizedState.Waiting &&
        parallelPriority.LastSessionTransitions.Count == 1 && parallelPriority.LastSessionTransitions[0].SessionId == "parallel-A" &&
        parallelPriority.LastSessionTransitions[0].Reason == "codex_turn_completed",
    "Per-session completion must survive aggregate WAITING precedence.");
var bareEnd = new StateReducer();
bareEnd.Apply(Hook(t, "UserPromptSubmit", "bare-end", turn: "turn-bare", thread: "thread-bare"));
bareEnd.Apply(Hook(t.AddSeconds(1), "SessionEnd", "bare-end", turn: "turn-bare", thread: "thread-bare"));
Require(bareEnd.State == K15NormalizedState.Normal, "Bare SessionEnd must not invent DONE.");
var doneThenEnd = new StateReducer();
doneThenEnd.Apply(Hook(t, "UserPromptSubmit", "done-end", turn: "turn-end", thread: "thread-end"));
doneThenEnd.Apply(Completion(t.AddSeconds(1), "thread-end", "turn-end"));
doneThenEnd.Apply(Hook(t.AddSeconds(2), "SessionEnd", "done-end", turn: "turn-end", thread: "thread-end"));
Require(doneThenEnd.State == K15NormalizedState.DonePendingAttention,
    "SessionEnd after proven DONE must preserve attention.");
var completionReplay = new StateReducer();
completionReplay.Rehydrate(new[] { Hook(t, "UserPromptSubmit", "rehydrate-done", cwd: @"C:\work\done", turn: "turn-done", thread: "thread-done"),
    Completion(t.AddSeconds(1), "thread-done", "turn-done") });
Require(completionReplay.State == K15NormalizedState.DonePendingAttention &&
        completionReplay.LastSessionTransitions.Count == 2 &&
        completionReplay.LastSessionTransitions[^1].IsRehydrated,
    "Replayed completion must be marked rehydrated.");
var parsedCompletion = JournalStateNormalizer.ParseInput("{\"schemaVersion\":\"k15-codex-completion/v1\",\"timestampUtc\":\"2026-08-25T00:00:00Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"turn_completed\",\"threadId\":\"thread-parser\",\"turnId\":\"turn-parser\",\"status\":\"completed\",\"turn\":\"MUST NOT PERSIST\"}");
Require(parsedCompletion is null, "Completion parser must reject unexpected payload fields.");
parsedCompletion = JournalStateNormalizer.ParseInput("{\"schemaVersion\":\"k15-codex-completion/v1\",\"timestampUtc\":\"2026-08-25T00:00:00Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"turn_completed\",\"threadId\":\"thread-parser\",\"turnId\":\"turn-parser\",\"status\":\"completed\"}");
Require(parsedCompletion?.CompletionStatus == "completed" && parsedCompletion.ThreadId == "thread-parser" &&
        parsedCompletion.TurnId == "turn-parser", "Valid completion parser shape changed.");
Require(JournalStateNormalizer.ParseInput("{\"schemaVersion\":\"k15-codex-completion/v1\",\"timestampUtc\":\"2026-08-25T00:00:00Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"turn_completed\",\"threadId\":\"thread-parser\",\"turnId\":\"turn-parser\",\"status\":\"inProgress\"}") is null,
    "Non-terminal inProgress completion must be rejected by the parser.");
var journalFixture = Path.Combine(Path.GetTempPath(), "vorotex-k15-event-journal-" + Guid.NewGuid().ToString("N"));
try
{
    EventJournal.SetTestDirectoryPath(journalFixture);
    EventJournal.SetDetailedLoggingEnabled(false);
    Require(!EventJournal.DetailedLoggingEnabled, "Journal filtering fixture must disable detailed logging.");
    EventJournal.Clear();
    EventJournal.Append(new
    {
        schemaVersion = "k15-codex-completion/v1",
        timestampUtc = t,
        source = "codex_stdio_bridge",
        @event = "turn_completed",
        threadId = "thread-journal",
        turnId = "turn-journal",
        status = "completed"
    });
    var completionLines = File.ReadAllLines(EventJournal.FilePath);
    Require(completionLines.Length == 1 && completionLines[0].Contains("k15-codex-completion/v1", StringComparison.Ordinal),
        "Valid sanitized completion must pass the real EventJournal.Append filtering path.");
    EventJournal.Append(new
    {
        schemaVersion = "k15-codex-completion/v1", timestampUtc = t, source = "codex_stdio_bridge",
        @event = "turn_completed", threadId = "thread-journal", turnId = "turn-journal", status = "completed",
        detail = "MUST NOT PASS"
    });
    EventJournal.Append(new
    {
        schemaVersion = "k15-codex-completion/v1", timestampUtc = t, source = "codex_stdio_bridge",
        @event = "turn_completed", threadId = "thread-journal", turnId = "turn-journal", status = "completed",
        decision = "accept"
    });
    EventJournal.Append(new
    {
        schemaVersion = "k15-codex-approval/v1", timestampUtc = t, source = "codex_stdio_bridge",
        @event = "approval_resolved", decision = "accept", rpcIdType = "number", rpcId = "1", status = "completed"
    });
    EventJournal.Append(new
    {
        schemaVersion = "k15-codex-approval/v1", timestampUtc = t, source = "codex_stdio_bridge",
        @event = "approval_resolved", decision = "accept", rpcIdType = "number", rpcId = "1"
    });
    Require(File.ReadAllLines(EventJournal.FilePath).Length == 2,
        "Schema-specific EventJournal filtering must reject cross-schema fields and retain valid approval.");
}
finally
{
    EventJournal.SetTestDirectoryPath(null);
    if (Directory.Exists(journalFixture))
        Directory.Delete(journalFixture, recursive: true);
}
var waitingCompletion = new StateReducer();
waitingCompletion.Apply(Hook(t, "UserPromptSubmit", "waiting-completion", turn: "turn-waiting", thread: "thread-waiting"));
waitingCompletion.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "waiting-completion", turn: "turn-waiting", thread: "thread-waiting"));
Require(waitingCompletion.Apply(Completion(t.AddSeconds(2), "thread-waiting", "turn-waiting")) is null &&
        waitingCompletion.State == K15NormalizedState.Waiting && waitingCompletion.LastSessionTransitions.Count == 0,
    "WAITING must not be treated as successful completion automatically.");
var endedCompletion = new StateReducer();
endedCompletion.Apply(Hook(t, "UserPromptSubmit", "ended-completion", turn: "turn-ended", thread: "thread-ended"));
endedCompletion.Apply(Hook(t.AddSeconds(1), "SessionEnd", "ended-completion", turn: "turn-ended", thread: "thread-ended"));
Require(endedCompletion.Apply(Completion(t.AddSeconds(2), "thread-ended", "turn-ended")) is null &&
        endedCompletion.State == K15NormalizedState.Normal && endedCompletion.LastSessionTransitions.Count == 0,
    "Ended session must not be resurrected by completion.");
reducer.Apply(Hook(t.AddSeconds(1), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "PermissionRequest must enter WAITING.");
var resolved = reducer.Apply(Approval(t.AddSeconds(1), "accept", turnId: "turn-approval"));
Require(resolved is null && reducer.State == K15NormalizedState.Waiting,
    "An approval without exact turn/thread correlation must not infer RUNNING.");
var exactApproval = new StateReducer();
exactApproval.Apply(Hook(t, "UserPromptSubmit", turn: "turn-approval"));
exactApproval.Apply(Hook(t.AddSeconds(1), "PermissionRequest", turn: "turn-approval"));
Require(exactApproval.LastSessionTransitions.Count == 1 &&
        exactApproval.LastSessionTransitions[0].SessionId == "session-main" &&
        exactApproval.LastSessionTransitions[0].Previous == K15NormalizedState.Running &&
        exactApproval.LastSessionTransitions[0].Current == K15NormalizedState.Waiting &&
        exactApproval.LastSessionTransitions[0].Reason == "codex_permission_request" &&
        !exactApproval.LastSessionTransitions[0].IsRehydrated,
    "Single-session approval must provide explicit live WAITING evidence before approval resolution.");
var bridgeTransition = exactApproval.Apply(Approval(t.AddSeconds(2), "accept", turnId: "turn-approval"));
Require(bridgeTransition?.Current == K15NormalizedState.Running &&
        bridgeTransition.Reason == "codex_approval_resolved" && exactApproval.State == K15NormalizedState.Running,
    "Exact sanitized accept must resume the matching WAITING session immediately.");
Require(exactApproval.LastSessionTransitions.Count == 1 &&
        exactApproval.LastSessionTransitions[0].SessionId == "session-main" &&
        exactApproval.LastSessionTransitions[0].Previous == K15NormalizedState.Waiting &&
        exactApproval.LastSessionTransitions[0].Current == K15NormalizedState.Running &&
        exactApproval.LastSessionTransitions[0].Reason == "codex_approval_resolved" &&
        !exactApproval.LastSessionTransitions[0].IsRehydrated,
    "Accepted approval must emit exactly one live same-session WAITING to RUNNING transition.");
var doneHold = new StateReducer();
doneHold.Apply(Hook(t, "UserPromptSubmit", "session-a", turn: "turn-a"));
doneHold.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "session-a", turn: "turn-a"));
doneHold.Apply(Hook(t.AddSeconds(2), "Stop", "session-b", turn: "turn-b"));
var doneHoldApproval = doneHold.Apply(Approval(t.AddSeconds(3), "accept", turnId: "turn-a"));
Require(doneHoldApproval?.Current == K15NormalizedState.DonePendingAttention &&
        doneHold.State == K15NormalizedState.DonePendingAttention &&
        doneHold.LastSessionTransitions.Count == 1 &&
        doneHold.LastSessionTransitions[0].SessionId == "session-a" &&
        doneHold.LastSessionTransitions[0].Reason == "codex_approval_resolved",
    "Per-session approval evidence must publish while DONE_UNREAD keeps the aggregate state.");
var twoWaiting = new StateReducer();
twoWaiting.Apply(Hook(t, "PermissionRequest", "session-a", turn: "turn-a"));
twoWaiting.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "session-b", turn: "turn-b"));
var unchangedApproval = twoWaiting.Apply(Approval(t.AddSeconds(2), "accept", turnId: "turn-a"));
Require(unchangedApproval is null && twoWaiting.State == K15NormalizedState.Waiting &&
        twoWaiting.LastSessionTransitions.Count == 1 &&
        twoWaiting.LastSessionTransitions[0].SessionId == "session-a" &&
        twoWaiting.Snapshot.ApprovalWaitingCount == 1,
    "Per-session event must publish when RecomputeAggregate returns null and B remains WAITING.");
var replayTransition = new StateReducer();
replayTransition.Rehydrate(new[]
{
    Hook(t, "PermissionRequest", "replay-session", turn: "replay-turn"),
    Approval(t.AddSeconds(1), "accept", turnId: "replay-turn")
});
Require(replayTransition.LastSessionTransitions.Count == 2 &&
        replayTransition.LastSessionTransitions[^1].IsRehydrated &&
        replayTransition.LastSessionTransitions[^1].Reason == "codex_approval_resolved",
    "Rehydrated session transitions must be explicitly marked and cannot masquerade as live evidence.");
var sessionJson = JsonSerializer.Serialize(new
{
    source = "state_normalizer", @event = "session_state_changed", plane = "per_session",
    sessionId = "opaque-session", previous = "WAITING", current = "RUNNING",
    reason = "codex_approval_resolved", isRehydrated = false,
    correlation = new { threadId = "opaque-thread", turnId = "opaque-turn", rpcIdType = "number", rpcId = "17" }
});
foreach (var forbidden in new[] { "prompt", "modelOutput", "toolArguments", "command", "rawProtocol", "credential", "token" })
    Require(!sessionJson.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
        $"Per-session event contract must not contain {forbidden}.");

var sessionDecision = new StateReducer();
sessionDecision.Apply(Hook(t, "UserPromptSubmit", turn: "turn-decision"));
sessionDecision.Apply(Hook(t.AddSeconds(1), "PermissionRequest", turn: "turn-decision"));
sessionDecision.Apply(Approval(t.AddSeconds(2), "decline", rpcId: "2", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Waiting,
    "decline must remain distinct and must not map to RUNNING.");
sessionDecision.Apply(Approval(t.AddSeconds(3), "cancel", rpcId: "3", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Waiting,
    "cancel must remain distinct and must not map to RUNNING.");
sessionDecision.Apply(Approval(t.AddSeconds(4), "acceptForSession", rpcId: "4", turnId: "turn-decision"));
Require(sessionDecision.State == K15NormalizedState.Running,
    "acceptForSession must resume the exact waiting session.");

var parallelApprovals = new StateReducer();
parallelApprovals.Apply(Hook(t, "UserPromptSubmit", "session-a", turn: "turn-a"));
parallelApprovals.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "session-a", turn: "turn-a"));
parallelApprovals.Apply(Hook(t.AddSeconds(2), "UserPromptSubmit", "session-b", turn: "turn-b"));
parallelApprovals.Apply(Hook(t.AddSeconds(3), "PermissionRequest", "session-b", turn: "turn-b"));
parallelApprovals.Apply(Approval(t.AddSeconds(4), "accept", rpcId: "5", turnId: "turn-b"));
Require(parallelApprovals.Snapshot.ApprovalWaitingCount == 1 && parallelApprovals.Snapshot.RunningCount == 1,
    "Parallel approvals must resolve only the matching turn.");
Require(parallelApprovals.Apply(Approval(t.AddSeconds(5), "accept", rpcId: "6", turnId: "turn-a"))?.Current == K15NormalizedState.Running,
    "The second parallel approval must resolve independently.");
Require(parallelApprovals.Apply(new StatusInputEvent(t.AddSeconds(6), "codex_stdio_bridge", "serverRequest/resolved",
        SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "7", TurnId: "turn-a")) is null,
    "Generic serverRequest/resolved must never map to RUNNING.");
var parsedApproval = JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:07Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"8","threadId":"session-a","turnId":"turn-a","itemId":"item-a"}
""");
Require(parsedApproval?.Decision == "accept" && parsedApproval.RpcId == "8" && parsedApproval.RpcIdType == "number" && parsedApproval.TurnId == "turn-a",
    "Status Lab must accept only the versioned sanitized approval schema.");
Require(JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:08Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"9","turnId":"turn-a","command":"MUST NOT BE ACCEPTED"}
""") is null,
    "Approval parser must reject arbitrary payload fields.");
Require(JournalStateNormalizer.ParseInput("""
{"schemaVersion":"k15-codex-approval/v1","timestampUtc":"2026-08-25T00:00:09Z","source":"codex_stdio_bridge","event":"approval_resolved","decision":"accept","rpcIdType":"number","rpcId":"10","turnId":42}
""") is null,
    "Approval parser must reject non-string correlation fields.");
var approvalTransition = reducer.Apply(Hook(t.AddSeconds(2), "PreToolUse"));
Require(reducer.State == K15NormalizedState.Running, "PreToolUse must resume RUNNING immediately after in-Codex approval.");
Require(approvalTransition?.Reason == "codex_pre_tool_use", "PreToolUse approval transition reason changed.");
reducer.Apply(Hook(t.AddSeconds(3), "PermissionRequest"));
Require(reducer.State == K15NormalizedState.Waiting, "Second PermissionRequest must enter WAITING.");
reducer.Apply(Hook(t.AddSeconds(4), "PostToolUse"));
Require(reducer.State == K15NormalizedState.Running, "PostToolUse remains a fallback RUNNING confirmation.");
reducer.Apply(Hook(t.AddSeconds(5), "Stop"));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Stop must enter DONE.");
reducer.Apply(Notification(t.AddSeconds(6), "windows_notification_added", 101, error: true));
Require(reducer.State == K15NormalizedState.DonePendingAttention, "Toast keywords must not create semantic ERROR.");
reducer.Apply(Notification(t.AddSeconds(7), "windows_notification_removed", 101));
Require(reducer.State == K15NormalizedState.DonePendingAttention,
    "Removing a completion toast must never acknowledge DONE_UNREAD.");

var manual = new StateReducer();
manual.Apply(Hook(t, "UserPromptSubmit", "manual-main"));
manual.Apply(Hook(t.AddSeconds(1), "PermissionRequest", "manual-main"));
Require(manual.State == K15NormalizedState.Waiting, "Manual-reset setup must be WAITING.");
var manualTransition = manual.Acknowledge(t.AddSeconds(2));
Require(manualTransition?.Current == K15NormalizedState.Normal && manual.State == K15NormalizedState.Normal,
    "Manual tray reset must clear WAITING/DONE to NORMAL.");

var preStop = new StateReducer(30);
preStop.Apply(Hook(t, "UserPromptSubmit", "pre-stop"));
preStop.Apply(Notification(t.AddSeconds(5), "windows_notification_added", 202));
preStop.Apply(Hook(t.AddSeconds(5.1), "Stop", "pre-stop"));
Require(preStop.State == K15NormalizedState.DonePendingAttention,
    "Completion toast arriving immediately before Stop must still correlate to DONE.");
preStop.Apply(Notification(t.AddSeconds(8), "windows_notification_removed", 202));
Require(preStop.State == K15NormalizedState.DonePendingAttention,
    "Removing a pre-Stop completion toast must not resolve DONE_UNREAD.");

var timeout = new StateReducer(30);
timeout.Apply(Hook(t, "UserPromptSubmit", "timeout-main"));
timeout.Apply(Hook(t.AddSeconds(1), "Stop", "timeout-main"));
Require(timeout.Tick(t.AddSeconds(30.9)) is null, "DONE fallback must not fire before 30 seconds from Stop.");
var timeoutTransition = timeout.Tick(t.AddSeconds(31.1));
Require(timeoutTransition?.Current == K15NormalizedState.Normal && timeoutTransition.Reason == "stale_attention_timeout",
    "Configured stale reset must use an explicit stale_attention_timeout reason.");

var parallel = new StateReducer();
parallel.Apply(Hook(t, "UserPromptSubmit", "main-A", @"D:\AI_AGENT_PROJECTS\agentloop-exchange-manual-win-001"));
Require(parallel.State == K15NormalizedState.Running, "Main A must be RUNNING.");
parallel.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "memory-B", @"C:\Users\Desktop\.codex-agentloop\memories"));
parallel.Apply(Hook(t.AddSeconds(2), "SessionEnd", "memory-B", @"C:\Users\Desktop\.codex-agentloop\memories"));
Require(parallel.State == K15NormalizedState.Running,
    "Background memories SessionEnd must not reset the foreground main session.");
Require(parallel.FocusedSessionId == "main-A", "Internal memories session must never steal focus.");

parallel.Apply(Hook(t.AddSeconds(3), "UserPromptSubmit", "main-C", @"D:\AI_AGENT_PROJECTS\other-task"));
Require(parallel.FocusedSessionId == "main-C", "Newest real task activity must take focus.");
parallel.Apply(Hook(t.AddSeconds(4), "SessionEnd", "main-C", @"D:\AI_AGENT_PROJECTS\other-task"));
Require(parallel.State == K15NormalizedState.Running && parallel.FocusedSessionId == "main-A",
    "Ending focused session C must fall back to still-active session A.");

var rehydrated = new StateReducer();
rehydrated.Rehydrate(new[]
{
    Hook(t, "UserPromptSubmit", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(1), "PermissionRequest", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(2), "PreToolUse", "rehydrate-main", @"D:\AI_AGENT_PROJECTS\rehydrate"),
    Hook(t.AddSeconds(3), "UserPromptSubmit", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories"),
    Hook(t.AddSeconds(4), "SessionEnd", "rehydrate-memory", @"C:\Users\Desktop\.codex-agentloop\memories")
});
Require(rehydrated.State == K15NormalizedState.Running,
    "Startup replay must recover a still-running main Codex session after approval.");
Require(rehydrated.FocusedSessionId == "rehydrate-main", "Rehydrate must recover foreground main session focus.");
Require(StateReducer.IsInternalCwd(@"C:\Users\Desktop\.codex-agentloop\memories"),
    "AgentLoop memories cwd must be classified internal.");
Require(!StateReducer.IsInternalCwd(@"D:\AI_AGENT_PROJECTS\task"), "Normal project cwd must not be internal.");

var ledger = new StateReducer(30);
ledger.Apply(Hook(t, "UserPromptSubmit", "A"));
ledger.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "B"));
ledger.Apply(Hook(t.AddSeconds(2), "Stop", "B"));
ledger.Apply(Hook(t.AddSeconds(3), "UserPromptSubmit", "C"));
ledger.Apply(Hook(t.AddSeconds(4), "PermissionRequest", "C"));
var snapshot = ledger.Snapshot;
Require(snapshot.RunningCount == 1 && snapshot.DoneUnreadCount == 1 && snapshot.ApprovalWaitingCount == 1 &&
        snapshot.AggregateState == K15NormalizedState.Waiting,
    "Scenario A: WAITING must outrank DONE and RUNNING across sessions.");
ledger.Apply(Hook(t.AddSeconds(5), "PreToolUse", "C"));
snapshot = ledger.Snapshot;
Require(snapshot.ApprovalWaitingCount == 0 && snapshot.DoneUnreadCount == 1 && snapshot.RunningCount == 2 &&
        snapshot.AggregateState == K15NormalizedState.DonePendingAttention,
    "Scenario A: resolved approval must reveal remaining DONE_UNREAD.");
ledger.Apply(Hook(t.AddSeconds(6), "UserPromptSubmit", "B"));
Require(ledger.Snapshot.DoneUnreadCount == 0 && ledger.State == K15NormalizedState.Running,
    "Scenario A: same-session UserPromptSubmit must acknowledge its old DONE.");

var unrelated = new StateReducer();
unrelated.Apply(Hook(t, "Stop", "A"));
unrelated.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "B"));
Require(unrelated.Snapshot.DoneUnreadCount == 1 && unrelated.State == K15NormalizedState.DonePendingAttention,
    "Scenarios B/D: RUNNING or a prompt in another session must not clear DONE_UNREAD.");

var priority = new StateReducer();
for (var i = 0; i < 10; i++) priority.Apply(Hook(t.AddSeconds(i), "UserPromptSubmit", $"run-{i}"));
for (var i = 0; i < 3; i++) priority.Apply(Hook(t.AddSeconds(11 + i), "Stop", $"done-{i}"));
priority.Apply(Hook(t.AddSeconds(20), "PermissionRequest", "approval"));
Require(priority.State == K15NormalizedState.Waiting, "Scenario C: approval must outrank all other attention.");

var blockedTimer = new StateReducer(30);
blockedTimer.Apply(Hook(t, "Stop", "done"));
blockedTimer.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "running"));
Require(blockedTimer.Tick(t.AddSeconds(40)) is null && blockedTimer.Snapshot.DoneUnreadCount == 1,
    "Scenario F: stale reset must be blocked while any session RUNS.");
blockedTimer.Apply(Hook(t.AddSeconds(41), "Stop", "running"));
Require(blockedTimer.Tick(t.AddSeconds(70)) is null && blockedTimer.Snapshot.DoneUnreadCount == 2,
    "Scenario H: a new zero-running interval must start a fresh timer.");
Require(blockedTimer.Tick(t.AddSeconds(72))?.Reason == "stale_attention_timeout" && blockedTimer.State == K15NormalizedState.Normal,
    "Scenarios G/H: stale reset must occur only after the fresh idle interval.");

var disabledTimer = new StateReducer(0);
disabledTimer.Apply(Hook(t, "Stop", "done"));
Require(disabledTimer.Tick(t.AddDays(2)) is null && disabledTimer.State == K15NormalizedState.DonePendingAttention,
    "Scenario J: zero must disable automatic stale reset.");

var ended = new StateReducer();
ended.Apply(Hook(t, "Stop", "A"));
ended.Apply(Hook(t.AddSeconds(1), "UserPromptSubmit", "C"));
ended.Apply(Hook(t.AddSeconds(2), "SessionEnd", "C"));
Require(ended.Snapshot.DoneUnreadCount == 1 && ended.State == K15NormalizedState.DonePendingAttention,
    "Scenario K: SessionEnd must not erase other-session attention.");
ended.Apply(Hook(t.AddSeconds(3), "SessionEnd", "A"));
Require(ended.Snapshot.DoneUnreadCount == 1 && ended.State == K15NormalizedState.DonePendingAttention &&
        ended.Snapshot.DriverSessionId == "A" &&
        ended.Snapshot.DriverReason == "aggregate_precedence_donependingattention",
    "Ended DONE_UNREAD must remain the aggregate driver with an explicit DONE precedence reason.");

var replayLedger = new StateReducer();
replayLedger.Rehydrate(new[]
{
    Hook(t, "UserPromptSubmit", "A"),
    Hook(t.AddSeconds(1), "UserPromptSubmit", "B"),
    Hook(t.AddSeconds(2), "Stop", "B"),
    Hook(t.AddSeconds(3), "UserPromptSubmit", "C"),
    Hook(t.AddSeconds(4), "PermissionRequest", "C")
});
Require(replayLedger.Snapshot.RunningCount == 1 && replayLedger.Snapshot.DoneUnreadCount == 1 &&
        replayLedger.Snapshot.ApprovalWaitingCount == 1 && replayLedger.State == K15NormalizedState.Waiting,
    "Scenario L: journal replay must restore multi-session aggregate attention.");

var config = StatusLabConfig.CreateDefault();
config.Validate();
Require(config.SchemaVersion == 5, "Canonical TOML schema must be v5.");
Require(config.WireColorOrder == WireColorOrder.RGB, "Physical K15 default must use RGB.");
Require(config.DoneAttentionTimeoutSeconds == 30, "DONE fallback timeout default must be 30 seconds.");
Require(config.StaleAttentionTimeoutSeconds == 18000, "Stale attention timeout default must be five hours.");
Require(config.Profiles.A.Color == "#FF0000" && config.Profiles.B.Color == "#0000FF", "Profile identity colors changed.");
Require(config.States.Running.Mode == K15LightingMode.FlowingWater, "RUNNING default must use Flowing Water.");
Require(config.States.Running.Palette == PaletteSource.Profile, "RUNNING must use active profile color.");
Require(config.States.Waiting.Mode == K15LightingMode.SingleColorBreathing && config.States.Waiting.Speed == 7,
    "WAITING must use fast single-color breathing speed 7.");
Require(config.States.Done.Mode == K15LightingMode.SingleColorBreathing && config.States.Done.Speed == 5,
    "DONE must use slower single-color breathing speed 5.");
Require(config.StopSignal.Mode == K15LightingMode.CycleBreathing && config.StopSignal.Palette == PaletteSource.ProfilePair,
    "STOP signal must use two-color Cycle breathing.");
Require(config.ActivationSignal.Enabled && config.ActivationSignal.Mode == K15LightingMode.CycleBreathing &&
        config.ActivationSignal.Palette == PaletteSource.ProfilePair && config.ActivationSignal.Speed == 7,
    "RGB activation must use fast two-color Cycle breathing.");
Require(!config.ProfileSwitch.Enabled && config.ProfileSwitch.DurationSeconds == 0,
    "RC1 must not compete with the keyboard-native profile switch animation.");
Require(StatusLabConfig.IsControlledPaletteMode(K15LightingMode.CycleBreathing),
    "Physically accepted Cycle breathing must be notifier-safe.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.MonoWater), "Native 0x83/Horse race must remain research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.TetrisBlocks), "Tetris must remain research-only for now.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Neon), "Neon must remain research-only.");
Require(!StatusLabConfig.IsControlledPaletteMode(K15LightingMode.Ambilight), "Ambilight must remain research-only.");

var runningA = config.RenderForProfile(0, config.States.Running);
var runningB = config.RenderForProfile(1, config.States.Running);
var stop = config.RenderForProfile(1, config.StopSignal);
Require(runningA.Colors.SequenceEqual(new[] { "#FF0000" }), "Profile A state renderer must use red only.");
Require(runningB.Colors.SequenceEqual(new[] { "#0000FF" }), "Profile B state renderer must use blue only.");
Require(stop.Colors.SequenceEqual(new[] { "#FF0000", "#0000FF" }),
    "profile_pair renderer must use canonical A then B colors.");

var pairRecord = K15HidProtocol.CreateEffectRecord(stop, WireColorOrder.RGB);
Require(pairRecord[3] == 0b00000011, "Two-color profile_pair must set palette mask 0x03.");
Require(pairRecord[4] == 0xFF && pairRecord[5] == 0 && pairRecord[6] == 0,
    "profile_pair slot 1 must encode profile A red.");
Require(pairRecord[7] == 0 && pairRecord[8] == 0 && pairRecord[9] == 0xFF,
    "profile_pair slot 2 must encode profile B blue.");

var toml = ConfigToml.Serialize(config);
Require(toml.Contains("schema_version = 5", StringComparison.Ordinal), "Canonical TOML must use schema v5.");
Require(toml.Contains("[behavior]", StringComparison.Ordinal) &&
        toml.Contains("stale_attention_timeout_seconds = 18000", StringComparison.Ordinal),
    "Canonical TOML must expose five-hour stale attention behavior.");
Require(toml.Contains("[stop_signal]", StringComparison.Ordinal), "Canonical TOML must include STOP overlay section.");
Require(toml.Contains("palette = \"profile_pair\"", StringComparison.Ordinal),
    "Canonical TOML must expose profile_pair palette source.");
Require(toml.Contains("effect = \"cycle_breathing\"", StringComparison.Ordinal),
    "Canonical TOML must expose physically accepted Cycle breathing.");
Require(toml.Contains("НИКОГДА программно не переключает", StringComparison.Ordinal),
    "Canonical TOML must document observe-only profile policy.");
Require(!toml.Contains("[states.running]\ncolor", StringComparison.Ordinal), "State TOML must not own colors.");
var roundTrip = ConfigToml.Parse(toml);
Require(roundTrip.StopSignal.Palette == PaletteSource.ProfilePair, "TOML round-trip lost STOP palette source.");
Require(roundTrip.StaleAttentionTimeoutSeconds == 18000, "TOML round-trip lost stale timeout.");

var existingV3WithoutBehavior = ConfigToml.Parse("schema_version = 3\n[states.done]\neffect = \"single_color_breathing\"\npalette = \"profile\"\n");
Require(existingV3WithoutBehavior.SchemaVersion == 5 && existingV3WithoutBehavior.StaleAttentionTimeoutSeconds == 18000,
    "Existing schema-v3 config without [behavior] must inherit the safe five-hour stale default.");

var oldV3 = ConfigToml.Parse("""
schema_version = 3
[behavior]
done_attention_timeout_seconds = 15
[profile_switch]
enabled = true
effect = "flowing_water"
palette = "profile"
brightness = 5
speed = 5
direction = 0
duration_seconds = 4
[activation]
enabled = true
effect = "flowing_water"
palette = "profile_pair"
brightness = 5
speed = 5
direction = 0
duration_seconds = 3
""");
Require(oldV3.SchemaVersion == 5 && oldV3.StaleAttentionTimeoutSeconds == 18000,
    "Legacy DONE timeout must not be silently reinterpreted as stale attention.");
Require(!oldV3.ProfileSwitch.Enabled && oldV3.ProfileSwitch.DurationSeconds == 0,
    "Exact beta profile-switch default must migrate to OFF in memory.");
Require(oldV3.ActivationSignal.Mode == K15LightingMode.CycleBreathing && oldV3.ActivationSignal.Speed == 7,
    "Exact beta activation default must migrate to fast Cycle breathing.");

var customV3 = ConfigToml.Parse("""
schema_version = 3
[behavior]
done_attention_timeout_seconds = 45
[profile_switch]
enabled = true
effect = "flowing_water"
palette = "profile"
brightness = 4
speed = 3
direction = 1
duration_seconds = 6
""");
Require(customV3.DoneAttentionTimeoutSeconds == 45 && customV3.StaleAttentionTimeoutSeconds == 18000 &&
        customV3.ProfileSwitch.Enabled && customV3.ProfileSwitch.DurationSeconds == 6,
    "Schema migration must preserve legacy data without reusing it as stale attention.");

var legacyV2 = ConfigToml.Parse("schema_version = 2\n[states.running]\neffect = \"flowing_water\"\n");
Require(legacyV2.SchemaVersion == 5, "Legacy schema v2 must migrate in memory without rewriting the file.");

var unsafeRejected = false;
try
{
    var unsafeConfig = StatusLabConfig.CreateDefault();
    unsafeConfig.ProfileSwitch.Mode = K15LightingMode.MonoWater;
    unsafeConfig.Validate();
}
catch (InvalidDataException ex)
{
    unsafeRejected = ex.Message.Contains("Lighting Lab", StringComparison.OrdinalIgnoreCase);
}
Require(unsafeRejected, "Research-only mode must fail notifier validation with a Lighting Lab hint.");

Require(K15HidProtocol.HorseRaceMode == 0x83 && K15HidProtocol.MonoWaterMode == 0x83,
    "Native 0x83 must preserve historical alias while using OEM Horse race naming.");
Require(K15HidProtocol.ModeCode(K15LightingMode.FlowingWater) == 0x82, "Flowing Water mode code changed.");
Require(K15HidProtocol.ModeCode(K15LightingMode.CycleBreathing) == 0x85, "Cycle breathing mode code changed.");

var palette = new LightingEffectConfig
{
    Mode = K15LightingMode.FlowingWater,
    Brightness = 6,
    Speed = 7,
    Direction = 0,
    Colors = ["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#800080", "#00FFFF", "#FFFFFF"],
    PaletteMask = 0b00000101
};
var paletteRecord = K15HidProtocol.CreateEffectRecord(palette, WireColorOrder.RGB);
Require(paletteRecord[3] == 0b00000101, "Lighting Lab explicit palette mask must be written verbatim.");
Require(paletteRecord[4] == 0xFF && paletteRecord[5] == 0 && paletteRecord[6] == 0, "Palette slot 1 RGB encoding changed.");
Require(paletteRecord[10] == 0 && paletteRecord[11] == 0 && paletteRecord[12] == 0xFF, "Palette slot 3 RGB encoding changed.");

var framed = K15HidProtocol.FrameReport(0x09, 0x12, 0, 0x0064, new byte[] { 1, 2, 3 });
Require(framed.Length == 41 && framed[0] == 0x06, "HID report framing changed.");
Require(K15HidProtocol.IsSupportedDevice(0xB6A4, 0x4100), "Physical K15 VID/PID must be accepted.");
Require(!K15HidProtocol.IsSupportedDevice(0x1234, 0x4100), "Unrelated VID must be rejected.");

Console.WriteLine("RC1 approval + session-aware reducer + 30s DONE + RGB policy + HID tests: PASS");
