using System.Text;
using System.Text.Json;
using Vorotex.K15.StatusLab;

internal static class CodexReadAckTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-08-25T00:00:00Z");
    private const string TestSourceInstanceId = "local:fc48c8bff668af187c6ae9b203b3321c";
    private static int _passed;
    private static void Check(bool value, string id)
    {
        if (!value) throw new InvalidOperationException("READ_ACK_" + id);
        _passed++;
        Console.WriteLine("READ_ACK_" + id + "=PASS");
    }
    private static StatusInputEvent Hook(string name, int second, string session = "S", string thread = "T", string turn = "U", string sourceInstanceId = TestSourceInstanceId) =>
        new(T.AddSeconds(second), "codex_hook", name, SessionId: session, ThreadId: thread, TurnId: turn,
            SourceInstanceId: sourceInstanceId);
    private static StatusInputEvent ApprovalRequest(int second, string thread = "T", string turn = "U", string item = "I", string rpc = "91", string family = "item/commandExecution", string reviewer = "user") =>
        new(T.AddSeconds(second), "codex_stdio_bridge", "approval_requested", SchemaVersion: "k15-codex-approval-request/v1",
            RpcIdType: "number", RpcId: rpc, ThreadId: thread, TurnId: turn, ItemId: item, RequestFamily: family,
            ApprovalsReviewer: reviewer, SourceInstanceId: TestSourceInstanceId);
    private static StateReducer Done(string session = "S", string thread = "T", string turn = "U")
    {
        var reducer = new StateReducer(0, T);
        reducer.Apply(Hook("UserPromptSubmit", 1, session, thread, turn));
        reducer.Apply(Hook("Stop", 2, session, thread, turn));
        return reducer;
    }
    private sealed class MutableLiveness : ICodexLivenessProvider
    {
        public CodexLivenessState State;
        public int Calls;
        public CodexLivenessState GetLiveness()
        {
            Calls++;
            return State;
        }
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
    private sealed class FocusedReader : ICodexFocusedCompletionReader
    {
        public CodexFocusedCompletionProof? Proof;
        public int Calls;
        public CodexFocusedCompletionProof? Read(CodexCompletionKey completion, DateTimeOffset nowUtc)
        {
            Calls++;
            return Proof is not null && Proof.Completion == completion ? Proof : null;
        }
    }
    private static CodexReadAckEvidence Evidence(StateReducer reducer, int second = 3) =>
        new(reducer.ReadAckCandidates.Single(), "local", T.AddSeconds(second), T.AddSeconds(second + 1), T.AddSeconds(second + 2));
    private static CodexUnreadState Parse(string json, string thread = "T") =>
        CodexUnreadStateReader.Parse(Encoding.UTF8.GetBytes(json), "local", T, T).ForThread(thread);
    private static string Store(string atom) => "{\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":" + atom + "},\"private\":\"must not escape\"}";
    private static string Canonical(string identities, string migration = "") =>
        "{\"electron-thread-read-state-v1\":{\"version\":1,\"unreadByIdentity\":" + identities + migration + "}}";
    private static CodexSessionSnapshot Session(K15NormalizedState state, string id = "S", string thread = "T") =>
        new(id, state, true, true, "C:\\synthetic", thread, "U", T);

    private static void FocusedCompletionAckTests()
    {
        var reducer = Done();
        var completion = reducer.ReadAckCandidates.Single();
        var focusedLineUtc = T.AddMilliseconds(2500);
        var focusedLog =
            $"{T:O} info websocket_reconnect_recovery_done currentConversationId=T rendererWindowAppearance=primary\n" +
            $"{focusedLineUtc:O} info [electron-message-handler] [desktop-notifications] received turn-complete " +
            "conversationId=T rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
            "rendererWindowId=1 rendererWindowVisible=true turnId=U\n";
        var proof = CodexDesktopFocusedCompletionReader.ParseLog(focusedLog, completion, 1234, T.AddSeconds(-1));
        Check(proof is not null && proof.DesktopProcessId == 1234 &&
              proof.DesktopCompletedUtc == focusedLineUtc, "FOCUSED_LOG_EXACT_PROOF");

        var backgroundLog = focusedLog.Replace("rendererWindowFocused=true", "rendererWindowFocused=false");
        Check(CodexDesktopFocusedCompletionReader.ParseLog(backgroundLog, completion, 1234, T.AddSeconds(-1)) is null,
            "BACKGROUND_COMPLETION_NEVER_FOCUSED_PROOF");

        var liveTurnStartLog =
            $"{T.AddSeconds(1):O} info [electron-message-handler] Reasoning summary turn-start config resolved " +
            "conversationId=T rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
            "rendererWindowId=1 rendererWindowVisible=true\n" +
            $"{focusedLineUtc:O} info [electron-message-handler] [desktop-notifications] received turn-complete " +
            "conversationId=T rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
            "rendererWindowId=1 rendererWindowVisible=true turnId=U\n";
        Check(CodexDesktopFocusedCompletionReader.ParseLog(liveTurnStartLog, completion, 1234,
                  T.AddSeconds(-1)) is not null,
            "FOCUSED_TURN_START_SEEDS_ACTIVE_CONVERSATION");

        var switchedAwayAfterTurnStart =
            $"{T.AddSeconds(1):O} info [electron-message-handler] Reasoning summary turn-start config resolved " +
            "conversationId=T rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
            "rendererWindowId=1 rendererWindowVisible=true\n" +
            $"{T.AddMilliseconds(1800):O} info [electron-message-handler] IAB_LIFECYCLE received browser sidebar owner sync " +
            "browserTabId=null conversationId=client originWebContentsId=1 ownerRoutePath=/local/OTHER windowId=1\n" +
            $"{focusedLineUtc:O} info [electron-message-handler] [desktop-notifications] received turn-complete " +
            "conversationId=T rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
            "rendererWindowId=1 rendererWindowVisible=true turnId=U\n";
        Check(CodexDesktopFocusedCompletionReader.ParseLog(switchedAwayAfterTurnStart, completion, 1234,
                  T.AddSeconds(-1)) is null,
            "LATER_ROUTE_CHANGE_INVALIDATES_TURN_START_ACTIVE_CONVERSATION");

        var wrongActiveLog =
            $"{T:O} info websocket_reconnect_recovery_done currentConversationId=OTHER rendererWindowAppearance=primary\n" +
            focusedLog.Split('\n')[1] + "\n";
        Check(CodexDesktopFocusedCompletionReader.ParseLog(wrongActiveLog, completion, 1234, T.AddSeconds(-1)) is null,
            "FOCUSED_WINDOW_WRONG_ACTIVE_THREAD_REJECTED");

        var routeLog =
            $"{T:O} info websocket_reconnect_recovery_done currentConversationId=OTHER rendererWindowAppearance=primary\n" +
            $"{T.AddSeconds(1):O} info [electron-message-handler] IAB_LIFECYCLE received browser sidebar owner sync " +
            "browserTabId=null conversationId=client originWebContentsId=1 ownerRoutePath=/local/T windowId=1\n" +
            focusedLog.Split('\n')[1] + "\n";
        Check(CodexDesktopFocusedCompletionReader.ParseLog(routeLog, completion, 1234, T.AddSeconds(-1)) is not null,
            "ROUTE_CHANGE_EXACT_THREAD_ESTABLISHES_ACTIVE_CONVERSATION");

        var settingsLog = routeLog.Replace("ownerRoutePath=/local/T", "ownerRoutePath=/settings/general");
        Check(CodexDesktopFocusedCompletionReader.ParseLog(settingsLog, completion, 1234, T.AddSeconds(-1)) is null,
            "NON_LOCAL_ROUTE_CLEARS_ACTIVE_CONVERSATION");

        var wrongTurn = focusedLog.Replace("turnId=U", "turnId=OTHER");
        Check(CodexDesktopFocusedCompletionReader.ParseLog(wrongTurn, completion, 1234, T.AddSeconds(-1)) is null,
            "FOCUSED_COMPLETION_TURN_MUST_MATCH");
        Check(CodexDesktopFocusedCompletionReader.ParseLog(focusedLog, completion with { CompletedUtc = T.AddSeconds(10) },
            1234, T.AddSeconds(-1)) is null, "FOCUSED_COMPLETION_TIMESTAMP_MUST_MATCH");
        Check(CodexDesktopFocusedCompletionReader.ParseLocalRoute("/local/T?x=1") == "T" &&
              CodexDesktopFocusedCompletionReader.ParseLocalRoute("/settings") is null,
            "LOCAL_ROUTE_PARSER_FAIL_CLOSED");
        Check(CodexDesktopFocusedCompletionReader.TryParseProcessId(
                  "codex-desktop-f9f0fbf3-f816-401b-a3c5-a272bf547200-3220-t0-i1-194136-0.log", out var parsedPid) &&
              parsedPid == 3220 &&
              !CodexDesktopFocusedCompletionReader.TryParseProcessId("codex-desktop-no-pid-t0-i1.log", out _),
            "T0_LOG_PID_PARSER");
        Check(CodexDesktopFocusedCompletionReader.IsCodexDesktopExecutable(
                  @"C:\Program Files\WindowsApps\OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe") &&
              !CodexDesktopFocusedCompletionReader.IsCodexDesktopExecutable(@"C:\Windows\ChatGPT.exe"),
            "CODEX_DESKTOP_PROCESS_IDENTITY_GUARD");

        var focusedFixtureRoot = Path.Combine(Path.GetTempPath(),
            "k15-focused-reader-" + Guid.NewGuid().ToString("N"));
        var focusedFixtureDay = Path.Combine(focusedFixtureRoot, "2026", "08", "25");
        Directory.CreateDirectory(focusedFixtureDay);
        var focusedFixtureName =
            "codex-desktop-30a922c7-d464-4e77-b8f8-a1aecc060cf7-1234-t0-i1-203835-0.log";
        var focusedFixturePath = Path.Combine(focusedFixtureDay, focusedFixtureName);
        var trustedDesktopPath =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        var trustedStartedUtc = T.AddMinutes(-1);

        void WriteFocusedFixture(string text, DateTimeOffset lastWriteUtc, DateTimeOffset? creationUtc = null)
        {
            File.WriteAllText(focusedFixturePath, text);
            File.SetCreationTimeUtc(focusedFixturePath, (creationUtc ?? T.AddSeconds(-30)).UtcDateTime);
            File.SetLastWriteTimeUtc(focusedFixturePath, lastWriteUtc.UtcDateTime);
        }

        try
        {
            WriteFocusedFixture(liveTurnStartLog, T.AddSeconds(3));
            var diagnosticReader = new CodexDesktopFocusedCompletionReader(
                focusedFixtureRoot,
                pid => pid == 1234
                    ? new(pid, trustedDesktopPath, trustedStartedUtc)
                    : null);
            var diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is not null &&
                  diagnostics.MatchedLogFileName == focusedFixtureName &&
                  diagnostics.CandidateLogs == 1 &&
                  diagnostics.ProcessLookupRejected == 0 &&
                  diagnostics.ExecutableRejected == 0 &&
                  diagnostics.ProcessCreationRejected == 0 &&
                  diagnostics.LastWriteRejected == 0 &&
                  diagnostics.ParseRejected == 0,
                "FOCUSED_READER_EXACT_CAPTURED_SHAPE_ALL_GATES_PASS");

            using (var activeWriter = new FileStream(
                       focusedFixturePath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
                Check(diagnostics.Proof is not null &&
                      diagnostics.IoRejected == 0 &&
                      diagnostics.MatchedLogFileName == focusedFixtureName,
                    "FOCUSED_READER_ACTIVE_WRITER_SHARED_READ");
            }

            WriteFocusedFixture(liveTurnStartLog, T.AddSeconds(3), T.AddMinutes(-10));
            diagnostics = new CodexDesktopFocusedCompletionReader(
                focusedFixtureRoot,
                pid => pid == 1234
                    ? new(pid, trustedDesktopPath, T.AddHours(-5))
                    : null)
                .ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is not null &&
                  diagnostics.ProcessCreationRejected == 0 &&
                  diagnostics.MatchedLogFileName == focusedFixtureName,
                "FOCUSED_READER_ROTATED_LOG_SAME_PROCESS_PASS");

            WriteFocusedFixture(liveTurnStartLog, T.AddSeconds(3), T.AddMinutes(10));
            diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.ProcessCreationRejected == 1,
                "FOCUSED_READER_CREATION_AFTER_COMPLETION_REJECTED");

            diagnostics = new CodexDesktopFocusedCompletionReader(focusedFixtureRoot, _ => null)
                .ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.CandidateLogs == 1 &&
                  diagnostics.ProcessLookupRejected == 1,
                "FOCUSED_READER_PROCESS_LOOKUP_REJECTION_IDENTIFIED");

            diagnostics = new CodexDesktopFocusedCompletionReader(
                focusedFixtureRoot,
                pid => new(pid, @"C:\Windows\ChatGPT.exe", trustedStartedUtc))
                .ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.ExecutableRejected == 1,
                "FOCUSED_READER_EXECUTABLE_REJECTION_IDENTIFIED");

            diagnostics = new CodexDesktopFocusedCompletionReader(
                focusedFixtureRoot,
                pid => new(pid, trustedDesktopPath, T.AddHours(1)))
                .ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.ProcessCreationRejected == 1,
                "FOCUSED_READER_PROCESS_CREATION_REJECTION_IDENTIFIED");

            WriteFocusedFixture(liveTurnStartLog, completion.CompletedUtc.AddSeconds(-10));
            diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.LastWriteRejected == 1,
                "FOCUSED_READER_LAST_WRITE_REJECTION_IDENTIFIED");

            WriteFocusedFixture(
                liveTurnStartLog.Replace("turnId=U", "turnId=OTHER"),
                T.AddSeconds(3));
            diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.ParseRejected == 1,
                "FOCUSED_READER_PARSE_REJECTION_IDENTIFIED");

            File.WriteAllText(focusedFixturePath, string.Empty);
            diagnostics = diagnosticReader.ReadWithDiagnostics(completion, T.AddSeconds(4));
            Check(diagnostics.Proof is null && diagnostics.FileMetadataRejected == 1,
                "FOCUSED_READER_FILE_METADATA_REJECTION_IDENTIFIED");
        }
        finally
        {
            if (Directory.Exists(focusedFixtureRoot))
                Directory.Delete(focusedFixtureRoot, true);
        }

        var unread = new Reader { Ids = [] };
        var focus = new FocusedReader { Proof = proof };
        var observer = new CodexFocusedReadAckObserver(new SingleCodexUnreadSourceRegistry(unread), focus, "local");
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3)).Count == 0,
            "FOCUSED_FIRST_NOUNREAD_NO_ACK");
        var focusedEvidence = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4)).Single();
        Check(focusedEvidence.DesktopCompletedUtc == focusedLineUtc &&
              focusedEvidence.FirstNoUnreadUtc < focusedEvidence.SecondNoUnreadUtc &&
              focusedEvidence.DesktopProcessId == 1234, "FOCUSED_SECOND_NOUNREAD_READY");
        Check(reducer.ApplyFocusedReadAck(focusedEvidence)?.Reason == "codex_focused_read_ack" &&
              reducer.State == K15NormalizedState.Normal, "FOCUSED_EXACT_ACK_APPLIES");

        reducer = Done();
        completion = reducer.ReadAckCandidates.Single();
        unread = new Reader { Ids = [] };
        focus = new FocusedReader { Proof = new(completion, T.AddMilliseconds(2500), 4321) };
        observer = new(new SingleCodexUnreadSourceRegistry(unread), focus, "local");
        observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(3));
        unread.Ids = ["T"];
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(4)).Count == 0,
            "TRANSIENT_HASUNREAD_RESETS_FOCUSED_CONFIRMATION");
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(5)).Count == 0,
            "PERSISTENT_HASUNREAD_NEVER_FOCUSED_ACKS");
        unread.Ids = [];
        Check(observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(6)).Count == 0,
            "POST_UNREAD_FIRST_NOUNREAD_NO_ACK");
        var afterTransientUnread = observer.Poll(reducer.ReadAckCandidates, T.AddSeconds(7)).Single();
        Check(afterTransientUnread.FirstNoUnreadUtc < afterTransientUnread.SecondNoUnreadUtc &&
              afterTransientUnread.DesktopProcessId == 4321,
            "POST_UNREAD_SECOND_NOUNREAD_RESTORES_FOCUSED_ACK");

        reducer = Done();
        completion = reducer.ReadAckCandidates.Single();
        var invalid = new CodexFocusedReadAckEvidence(completion, "remote", T.AddMilliseconds(2500),
            T.AddSeconds(3), T.AddSeconds(4), 1234);
        Check(reducer.ApplyFocusedReadAck(invalid) is null && reducer.State == K15NormalizedState.DonePendingAttention,
            "FOCUSED_ACK_REJECTS_NONLOCAL_HOST");
        invalid = new(completion, "local", T.AddMilliseconds(2500),
            T.AddMilliseconds(2400), T.AddSeconds(4), 1234);
        Check(reducer.ApplyFocusedReadAck(invalid) is null, "FOCUSED_ACK_REJECTS_BAD_CHRONOLOGY");

        var focusedCheckpointJson =
            $$"""{"timestampUtc":"{{T.AddSeconds(5):O}}","source":"state_normalizer","event":"focused_read_ack_evidence","reason":"codex_focused_read_ack","host":"local","sessionId":"S","threadId":"T","turnId":"U","sourceInstanceId":"{{TestSourceInstanceId}}","runtimeEpoch":"{{completion.RuntimeEpoch}}","completionGeneration":{{completion.Generation}},"completedUtc":"{{completion.CompletedUtc:O}}","desktopCompletedUtc":"{{T.AddMilliseconds(2500):O}}","firstNoUnreadUtc":"{{T.AddSeconds(3):O}}","secondNoUnreadUtc":"{{T.AddSeconds(4):O}}","desktopProcessId":1234}""";
        var checkpoint = JournalStateNormalizer.ParseReadAckCheckpoint(focusedCheckpointJson);
        Check(checkpoint is not null && checkpoint.ThreadId == "T" &&
              checkpoint.AcknowledgedUtc == T.AddSeconds(4), "FOCUSED_ACK_DURABLE_CHECKPOINT");
        Check(JournalStateNormalizer.ParseReadAckCheckpoint(
              focusedCheckpointJson.Replace("\"desktopProcessId\":1234", "\"desktopProcessId\":0")) is null,
            "FOCUSED_ACK_CHECKPOINT_REJECTS_BAD_PID");
        Check(JournalStateNormalizer.ParseReadAckCheckpoint(
              focusedCheckpointJson[..^1] + ",\"unexpected\":true}") is null,
            "FOCUSED_ACK_CHECKPOINT_REJECTS_UNEXPECTED_FIELDS");
    }

    public static void Run()
    {
        SourceIdentityTests();
        FocusedCompletionAckTests();
        Check(CodexSourceIdentity.ForHome(@"C:\Users\Tester\.codex") == TestSourceInstanceId,
            "SOURCE_IDENTITY_WINDOWS_GOLDEN_VECTOR");
        NormalizerLivenessTests();
        var authority = new StateReducer(0, T);
        authority.Apply(Hook("UserPromptSubmit", 1));
        authority.Apply(Hook("PermissionRequest", 2));
        Check(authority.State == K15NormalizedState.Running, "RAW_PERMISSION_REQUEST_NOT_WAITING");
        authority.Apply(ApprovalRequest(3));
        Check(authority.State == K15NormalizedState.Waiting && authority.LastSessionTransitions.Single().PermissionEvidence?.RpcId == "91" &&
            authority.LastSessionTransitions.Single().PermissionEvidence?.RequestFamily == "item/commandExecution", "TYPED_COMMAND_REQUEST_WAITING");
        authority.Apply(new(T.AddSeconds(4), "codex_stdio_bridge", "approval_resolved", SchemaVersion: "k15-codex-approval/v1",
            Decision: "accept", RpcIdType: "number", RpcId: "91", ThreadId: "T", TurnId: "U", ItemId: "I",
            SourceInstanceId: TestSourceInstanceId));
        Check(authority.State == K15NormalizedState.Running, "EXACT_TYPED_ACCEPT_RESUMES");
        var permissionsAuthority = new StateReducer(0, T);
        permissionsAuthority.Apply(Hook("UserPromptSubmit", 1));
        permissionsAuthority.Apply(ApprovalRequest(2, family: "item/permissions"));
        Check(permissionsAuthority.State == K15NormalizedState.Waiting &&
              permissionsAuthority.LastSessionTransitions.Single().PermissionEvidence?.RequestFamily == "item/permissions",
            "TYPED_PERMISSIONS_REQUEST_WAITING");

        var liveDesktopIdentity = new StateReducer(0, T);
        liveDesktopIdentity.Apply(Hook("UserPromptSubmit", 1, session: "LIVE", thread: "", turn: "LIVE-TURN"));
        liveDesktopIdentity.Apply(ApprovalRequest(2, thread: "LIVE", turn: "LIVE-TURN",
            item: "LIVE-I", rpc: "201", family: "item/permissions"));
        Check(liveDesktopIdentity.State == K15NormalizedState.Waiting &&
              liveDesktopIdentity.SessionSnapshots.Single().ThreadId == "LIVE" &&
              liveDesktopIdentity.LastSessionTransitions.Single().ThreadId == "LIVE",
            "DESKTOP_SESSION_ID_PROMOTED_TO_TYPED_THREAD_FOR_WAITING");
        liveDesktopIdentity.Apply(new(T.AddSeconds(3), "codex_stdio_bridge", "approval_resolved",
            SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "201",
            ThreadId: "LIVE", TurnId: "LIVE-TURN", ItemId: "LIVE-I", SourceInstanceId: TestSourceInstanceId));
        Check(liveDesktopIdentity.State == K15NormalizedState.Running,
            "DESKTOP_PROMOTED_THREAD_ACCEPT_RESUMES_EXACTLY");

        var wrongDesktopThread = new StateReducer(0, T);
        wrongDesktopThread.Apply(Hook("UserPromptSubmit", 1, session: "LIVE", thread: "", turn: "LIVE-TURN"));
        wrongDesktopThread.Apply(ApprovalRequest(2, thread: "OTHER", turn: "LIVE-TURN",
            item: "LIVE-I", rpc: "202", family: "item/permissions"));
        Check(wrongDesktopThread.State == K15NormalizedState.Running &&
              wrongDesktopThread.SessionSnapshots.Single().ThreadId.Length == 0,
            "DESKTOP_SESSION_ID_FALLBACK_REQUIRES_EXACT_TYPED_THREAD");

        var liveDesktopReplay = new StateReducer(0, T);
        liveDesktopReplay.Rehydrate([
            Hook("UserPromptSubmit", 1, session: "LIVE", thread: "", turn: "LIVE-TURN"),
            ApprovalRequest(2, thread: "LIVE", turn: "LIVE-TURN", item: "LIVE-I", rpc: "203", family: "item/permissions")
        ]);
        Check(liveDesktopReplay.State == K15NormalizedState.Waiting &&
              liveDesktopReplay.SessionSnapshots.Single().ThreadId == "LIVE" &&
              liveDesktopReplay.LastSessionTransitions.Last().IsRehydrated,
            "DESKTOP_SESSION_ID_TYPED_THREAD_REHYDRATES_DETERMINISTICALLY");

        var ambiguousDesktopIdentity = new StateReducer(0, T);
        ambiguousDesktopIdentity.Apply(Hook("UserPromptSubmit", 1, session: "LIVE", thread: "", turn: "LIVE-TURN"));
        ambiguousDesktopIdentity.Apply(Hook("UserPromptSubmit", 1, session: "OTHER", thread: "LIVE", turn: "LIVE-TURN"));
        ambiguousDesktopIdentity.Apply(ApprovalRequest(2, thread: "LIVE", turn: "LIVE-TURN",
            item: "LIVE-I", rpc: "204", family: "item/permissions"));
        Check(ambiguousDesktopIdentity.State == K15NormalizedState.Running &&
              ambiguousDesktopIdentity.SessionSnapshots.All(session => session.State == K15NormalizedState.Running) &&
              ambiguousDesktopIdentity.SessionSnapshots.Single(session => session.SessionId == "LIVE").ThreadId.Length == 0,
            "DESKTOP_SESSION_ID_FALLBACK_AMBIGUOUS_FAILS_CLOSED");

        permissionsAuthority.Apply(new(T.AddSeconds(3), "codex_hook", "PreToolUse",
            SessionId: "S", ThreadId: "T", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId));
        Check(permissionsAuthority.State == K15NormalizedState.Waiting &&
              permissionsAuthority.LastSessionTransitions.Count == 0 &&
              permissionsAuthority.SessionSnapshots.Single().LastActivityUtc == T.AddSeconds(3),
            "PERMISSIONS_PRE_TOOL_REQUEST_STAYS_WAITING");
        permissionsAuthority.Apply(new(T.AddSeconds(4), "codex_hook", "PostToolUse",
            SessionId: "S", ThreadId: "T", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId));
        Check(permissionsAuthority.State == K15NormalizedState.Running,
            "PERMISSIONS_POST_TOOL_USE_RESUMES");

        var commandWaitDoesNotUsePermissionsCarveout = new StateReducer(0, T);
        commandWaitDoesNotUsePermissionsCarveout.Apply(Hook("UserPromptSubmit", 1));
        commandWaitDoesNotUsePermissionsCarveout.Apply(ApprovalRequest(2, family: "item/commandExecution"));
        commandWaitDoesNotUsePermissionsCarveout.Apply(new(T.AddSeconds(3), "codex_hook", "PreToolUse",
            SessionId: "S", ThreadId: "T", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId));
        Check(commandWaitDoesNotUsePermissionsCarveout.State == K15NormalizedState.Running,
            "REQUEST_PERMISSIONS_PRE_TOOL_ONLY_PRESERVES_PERMISSIONS_WAIT");

        var missingToolNameProvenance = new StateReducer(0, T);
        missingToolNameProvenance.Apply(Hook("UserPromptSubmit", 1));
        missingToolNameProvenance.Apply(ApprovalRequest(2, family: "item/permissions"));
        missingToolNameProvenance.Apply(new(T.AddSeconds(3), "codex_hook", "PreToolUse",
            SessionId: "S", ThreadId: "T", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: false, SourceInstanceId: TestSourceInstanceId));
        Check(missingToolNameProvenance.State == K15NormalizedState.Running,
            "PERMISSIONS_PRE_TOOL_CARVEOUT_REQUIRES_TOOLNAME_PROVENANCE");

        var conflictingPermissionsThread = new StateReducer(0, T);
        conflictingPermissionsThread.Apply(Hook("UserPromptSubmit", 1));
        conflictingPermissionsThread.Apply(ApprovalRequest(2, family: "item/permissions"));
        conflictingPermissionsThread.Apply(new(T.AddSeconds(3), "codex_hook", "PreToolUse",
            SessionId: "S", ThreadId: "OTHER", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId));
        Check(conflictingPermissionsThread.State == K15NormalizedState.Running &&
              conflictingPermissionsThread.LastSessionTransitions.Single().Reason == "codex_pre_tool_use",
            "PERMISSIONS_PRE_TOOL_CARVEOUT_REQUIRES_NONCONFLICTING_THREAD");

        var emptyThreadPermissionsPreTool = new StateReducer(0, T);
        emptyThreadPermissionsPreTool.Apply(Hook("UserPromptSubmit", 1));
        emptyThreadPermissionsPreTool.Apply(ApprovalRequest(2, family: "item/permissions"));
        emptyThreadPermissionsPreTool.Apply(new(T.AddSeconds(3), "codex_hook", "PreToolUse",
            SessionId: "S", ThreadId: "", TurnId: "U", ToolName: "request_permissions",
            ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId));
        Check(emptyThreadPermissionsPreTool.State == K15NormalizedState.Waiting &&
              emptyThreadPermissionsPreTool.SessionSnapshots.Single().ThreadId == "T",
            "PERMISSIONS_PRE_TOOL_EMPTY_THREAD_PRESERVES_EXACT_WAIT");

        var permissionsPreToolReplay = new StateReducer(0, T);
        permissionsPreToolReplay.Rehydrate([
            Hook("UserPromptSubmit", 1),
            ApprovalRequest(2, family: "item/permissions"),
            new(T.AddSeconds(3), "codex_hook", "PreToolUse",
                SessionId: "S", ThreadId: "T", TurnId: "U", ToolName: "request_permissions",
                ToolNameProvided: true, SourceInstanceId: TestSourceInstanceId)
        ]);
        Check(permissionsPreToolReplay.State == K15NormalizedState.Waiting &&
              permissionsPreToolReplay.LastSessionTransitions.Last().Current == K15NormalizedState.Waiting,
            "PERMISSIONS_PRE_TOOL_REHYDRATES_WAITING");

        var automaticPermissions = new StateReducer(0, T);
        automaticPermissions.Apply(Hook("UserPromptSubmit", 1));
        automaticPermissions.Apply(ApprovalRequest(2, family: "item/permissions", reviewer: "auto_review"));
        Check(automaticPermissions.State == K15NormalizedState.Running,
            "AUTO_REVIEW_PERMISSIONS_NEVER_WAIT");
        var automatic = new StateReducer(0, T); automatic.Apply(Hook("UserPromptSubmit", 1));
        automatic.Apply(ApprovalRequest(2, reviewer: "auto_review"));
        automatic.Apply(new(T.AddSeconds(3), "codex_stdio_bridge", "approval_resolved", SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "91", ThreadId: "T", TurnId: "U", ItemId: "I", SourceInstanceId: TestSourceInstanceId));
        Check(automatic.State == K15NormalizedState.Running, "AUTO_REVIEW_REQUEST_AND_ACCEPT_NEVER_WAIT");
        var unknownReviewer = new StateReducer(0, T); unknownReviewer.Apply(Hook("UserPromptSubmit", 1));
        unknownReviewer.Apply(ApprovalRequest(2, reviewer: ""));
        Check(unknownReviewer.State == K15NormalizedState.Running, "UNKNOWN_REVIEWER_FAILS_CALM");
        authority.Apply(ApprovalRequest(5, "T", "OTHER", "I2", "92", "item/fileChange"));
        Check(authority.State == K15NormalizedState.Running, "WRONG_TURN_REQUEST_FAILS_CALM");
        var typedReplayReducer = new StateReducer(0, T);
        typedReplayReducer.Rehydrate([Hook("UserPromptSubmit", 1), ApprovalRequest(3, family: "item/fileChange")]);
        Check(typedReplayReducer.State == K15NormalizedState.Waiting && typedReplayReducer.LastSessionTransitions.Last().Reason == "codex_permission_request",
            "TYPED_REQUEST_REHYDRATES_DETERMINISTICALLY");
        typedReplayReducer.Apply(Hook("PostToolUse", 4));
        Check(typedReplayReducer.State == K15NormalizedState.Running, "POST_TOOL_USE_CLEARS_WAITING");
        var permissionsReplay = new StateReducer(0, T);
        permissionsReplay.Rehydrate([Hook("UserPromptSubmit", 1), ApprovalRequest(2, family: "item/permissions")]);
        Check(permissionsReplay.State == K15NormalizedState.Waiting &&
              permissionsReplay.LastSessionTransitions.Last().PermissionEvidence?.RequestFamily == "item/permissions" &&
              permissionsReplay.LastSessionTransitions.Last().IsRehydrated,
            "TYPED_PERMISSIONS_REHYDRATES_DETERMINISTICALLY");
        typedReplayReducer.Apply(ApprovalRequest(3, rpc: "92"));
        Check(typedReplayReducer.State == K15NormalizedState.Running,
            "LATE_REQUEST_AFTER_POST_TOOL_USE_IGNORED");
        var reorderedHook = new StateReducer(0, T);
        reorderedHook.Apply(Hook("UserPromptSubmit", 1));
        reorderedHook.Apply(Hook("PostToolUse", 10));
        reorderedHook.Apply(Hook("PostToolUse", 8));
        reorderedHook.Apply(ApprovalRequest(9, rpc: "93"));
        Check(reorderedHook.State == K15NormalizedState.Running &&
              reorderedHook.SessionSnapshots.Single().LastActivityUtc == T.AddSeconds(10),
            "DELAYED_HOOK_CANNOT_REWIND_ACTIVITY_OR_REOPEN_WAITING");
        var stoppedApproval = new StateReducer(0, T);
        stoppedApproval.Apply(Hook("UserPromptSubmit", 1));
        stoppedApproval.Apply(Hook("Stop", 4));
        stoppedApproval.Apply(ApprovalRequest(3));
        Check(stoppedApproval.State == K15NormalizedState.DonePendingAttention,
            "LATE_REQUEST_AFTER_STOP_IGNORED");
        var resumedApproval = new StateReducer(0, T);
        resumedApproval.Apply(Hook("UserPromptSubmit", 1));
        resumedApproval.Apply(ApprovalRequest(2));
        resumedApproval.Apply(Hook("PreToolUse", 3));
        resumedApproval.Apply(new(T.AddSeconds(4), "codex_stdio_bridge", "approval_resolved",
            SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "91",
            ThreadId: "T", TurnId: "U", ItemId: "I", SourceInstanceId: TestSourceInstanceId));
        Check(resumedApproval.State == K15NormalizedState.Running &&
              resumedApproval.LastSessionTransitions.Count == 0,
            "STALE_RESOLUTION_AFTER_EXECUTION_RESUMED_IGNORED");
        var wrongSourceResolution = new StateReducer(0, T);
        wrongSourceResolution.Apply(Hook("UserPromptSubmit", 1));
        wrongSourceResolution.Apply(ApprovalRequest(2));
        wrongSourceResolution.Apply(new(T.AddSeconds(3), "codex_stdio_bridge", "approval_resolved",
            SchemaVersion: "k15-codex-approval/v1", Decision: "accept", RpcIdType: "number", RpcId: "91",
            ThreadId: "T", TurnId: "U", ItemId: "I", SourceInstanceId: "local:0123456789abcdef0123456789abcdef"));
        Check(wrongSourceResolution.State == K15NormalizedState.Waiting,
            "RESOLUTION_REQUIRES_EXACT_SOURCE_IDENTITY");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_resolved\",\"schemaVersion\":\"k15-codex-approval/v1\",\"decision\":\"accept\",\"rpcIdType\":\"number\",\"rpcId\":\"91\"}") is not null,
            "LEGACY_RESOLUTION_REMAINS_DIAGNOSTIC_PARSEABLE");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/commandExecution\",\"approvalsReviewer\":\"user\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"I\",\"sourceInstanceId\":\"" + TestSourceInstanceId + "\"}") is not null,
            "REQUEST_REQUIRES_VALID_SOURCE_IDENTITY");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/permissions\",\"approvalsReviewer\":\"user\",\"rpcIdType\":\"number\",\"rpcId\":\"2\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"P\",\"sourceInstanceId\":\"" + TestSourceInstanceId + "\"}") is not null,
            "PERMISSIONS_REQUEST_REQUIRES_VALID_SOURCE_IDENTITY");
        Check(JournalStateNormalizer.ParseInput("{\"schemaVersion\":\"k15-codex-permissions-approval-diagnostic/v1\",\"source\":\"codex_stdio_bridge\",\"event\":\"permissions_approval_observed\",\"requestFamily\":\"item/permissions\",\"rpcIdType\":\"number\",\"rpcId\":\"2\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"P\",\"requestObservedAtUtc\":\"2026-08-25T00:00:01.000Z\",\"responseObservedAtUtc\":\"2026-08-25T00:00:02.000Z\",\"scope\":\"turn\",\"strictAutoReview\":false}") is null,
            "PERMISSIONS_DIAGNOSTIC_REMAINS_STATE_NEUTRAL");
        var ambiguous = new StateReducer(0, T);
        ambiguous.Apply(Hook("UserPromptSubmit", 1, "A"));
        ambiguous.Apply(Hook("UserPromptSubmit", 2, "B"));
        ambiguous.Apply(ApprovalRequest(3));
        Check(ambiguous.SessionSnapshots.All(s => s.State == K15NormalizedState.Running), "AMBIGUOUS_TYPED_REQUEST_FAILS_CALM");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/commandExecution\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"I\",\"command\":\"PRIVATE_MARKER\"}") is null,
            "REQUEST_PRIVATE_PAYLOAD_REJECTED");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/commandExecution\",\"approvalsReviewer\":\"guardian_subagent\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"I\"}") is null,
            "REQUEST_INVALID_REVIEWER_REJECTED");
        Check(JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/commandExecution\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"turnId\":\"U\",\"itemId\":\"I\"}") is null &&
            JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/commandExecution\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"threadId\":\"T\",\"itemId\":\"I\"}") is null &&
            JournalStateNormalizer.ParseInput("{\"timestampUtc\":\"2026-08-25T00:00:01Z\",\"source\":\"codex_stdio_bridge\",\"event\":\"approval_requested\",\"schemaVersion\":\"k15-codex-approval-request/v1\",\"requestFamily\":\"item/permissions\",\"rpcIdType\":\"number\",\"rpcId\":\"1\",\"threadId\":\"T\",\"turnId\":\"U\",\"itemId\":\"I\"}") is null,
            "REQUEST_MISSING_ID_AND_PERMISSIONS_SCHEMA_REJECTED");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[\"T\"]}}")) == CodexUnreadState.HasUnread, "CANONICAL_SINGLE_IDENTITY_HAS_UNREAD");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[]}}")) == CodexUnreadState.NoUnread, "CANONICAL_SINGLE_IDENTITY_NO_UNREAD");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[\"T\"]},\"identity-b\":{\"host-a\":[]}}")) == CodexUnreadState.Unknown, "CANONICAL_MULTIPLE_IDENTITIES_UNKNOWN");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[],\"host-b\":[]}}")) == CodexUnreadState.Unknown, "CANONICAL_MULTIPLE_HOSTS_UNKNOWN");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[],\"host-b\":[\"T\"]}}", ",\"legacyMigration\":{\"adoptedHostIds\":{\"local\":\"host-b\"}}")) == CodexUnreadState.HasUnread, "CANONICAL_NESTED_ADOPTED_HOST");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[],\"host-b\":[]}}", ",\"legacyMigration\":{\"adoptedHostIds\":{\"local\":\"host-missing\"}}")) == CodexUnreadState.Unknown, "CANONICAL_ADOPTED_HOST_MISSING");
        Check(Parse("{\"electron-thread-read-state-v1\":{\"version\":1,\"unreadByIdentity\":{\"identity-a\":{\"host-a\":[],\"host-b\":[\"T\"]}}},\"legacyMigration\":{\"adoptedHostIds\":{\"local\":\"host-b\"}}}") == CodexUnreadState.Unknown, "ROOT_LEVEL_ADOPTED_HOST_REJECTED");
        Check(Parse("{\"electron-thread-read-state-v1\":{\"version\":2},\"electron-persisted-atom-state\":{\"unread-thread-ids-by-host-v1\":{\"local\":[\"T\"]}}}") == CodexUnreadState.Unknown, "CANONICAL_NO_LEGACY_FALLBACK");
        Check(Parse("{\"electron-thread-read-state-v1\":{\"version\":1,\"unreadByIdentity\":null}}") == CodexUnreadState.Unknown, "CANONICAL_MALFORMED_UNKNOWN");
        Check(Parse(Store("{\"local\":[]}")) == CodexUnreadState.NoUnread, "ABSENT_CANONICAL_USES_LEGACY");
        Check(Parse(Canonical("{\"identity-a\":{\"host-a\":[\"T\",\"T\"]}}")) == CodexUnreadState.Unknown, "CANONICAL_DUPLICATE_THREAD_REJECTED");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.Normal)], "S", CodexUnreadState.Unknown).State == CodexPetVisualState.Idle, "PET_NORMAL_IDLE");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.Running)], "S", CodexUnreadState.Unknown).State == CodexPetVisualState.Running, "PET_RUNNING");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.Waiting)], "S", CodexUnreadState.Unknown).State == CodexPetVisualState.Waiting, "PET_WAITING");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.HasUnread).State == CodexPetVisualState.Review, "PET_REVIEW_EXACT_THREAD");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.NoUnread).State == CodexPetVisualState.Review, "PET_DONE_NOUNREAD_STAYS_REVIEW_UNTIL_REDUCER_ACK");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.Unknown).State == CodexPetVisualState.Review, "PET_DONE_UNKNOWN_STAYS_REVIEW_UNTIL_REDUCER_ACK");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.NoUnread).State == CodexPetVisualState.Review, "PET_DONE_COMPAT_OVERLOAD_FOLLOWS_REDUCER");
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.Running, "A"), Session(K15NormalizedState.Waiting, "B")], null, CodexUnreadState.HasUnread).Reason == "aggregate_waiting", "PET_MULTI_SESSION_WAITING_PRECEDENCE");
        var runningA = Session(K15NormalizedState.Running, "A", "TA");
        var runningB = Session(K15NormalizedState.Running, "B", "TB");
        var waitingB = Session(K15NormalizedState.Waiting, "B", "TB");
        var doneA = Session(K15NormalizedState.DonePendingAttention, "A", "TA");
        var doneB = Session(K15NormalizedState.DonePendingAttention, "B", "TB");
        Check(CodexPetAdapter.Map([runningA, runningB], new Dictionary<string, CodexUnreadState>()).State == CodexPetVisualState.Running, "PET_MULTI_RUNNING_PRECEDENCE");
        Check(CodexPetAdapter.Map([runningA, waitingB], new Dictionary<string, CodexUnreadState>()).State == CodexPetVisualState.Waiting, "PET_WAITING_PLUS_RUNNING_PRECEDENCE");
        Check(CodexPetAdapter.Map([runningA, doneB], new Dictionary<string, CodexUnreadState> { ["TB"] = CodexUnreadState.HasUnread }).State == CodexPetVisualState.Review, "PET_REVIEW_PLUS_RUNNING_PRECEDENCE");
        Check(CodexPetAdapter.Map([waitingB, doneA], new Dictionary<string, CodexUnreadState> { ["TA"] = CodexUnreadState.HasUnread }).State == CodexPetVisualState.Waiting, "PET_WAITING_PLUS_REVIEW_PRECEDENCE");
        Check(CodexPetAdapter.Map([runningA, doneB], new Dictionary<string, CodexUnreadState> { ["TB"] = CodexUnreadState.NoUnread }).State == CodexPetVisualState.Review, "PET_DONE_NOUNREAD_PRECEDES_RUNNING_UNTIL_REDUCER_ACK");
        Check(CodexPetAdapter.Map([runningA, doneB], new Dictionary<string, CodexUnreadState> { ["TB"] = CodexUnreadState.Unknown }).State == CodexPetVisualState.Review, "PET_DONE_UNKNOWN_PRECEDES_RUNNING_UNTIL_REDUCER_ACK");
        Check(CodexPetAdapter.Map([doneA, doneB], new Dictionary<string, CodexUnreadState> { ["TA"] = CodexUnreadState.NoUnread, ["TB"] = CodexUnreadState.HasUnread }).State == CodexPetVisualState.Review, "PET_MULTI_DONE_ANY_UNREAD_REVIEW");
        Check(CodexPetAdapter.Map([doneA, doneB], new Dictionary<string, CodexUnreadState> { ["TA"] = CodexUnreadState.Unknown, ["TB"] = CodexUnreadState.Unavailable }).State == CodexPetVisualState.Review, "PET_MULTI_DONE_UNKNOWN_STAYS_REVIEW_UNTIL_REDUCER_ACK");
        var ended = new CodexSessionSnapshot("ended", K15NormalizedState.Normal, false, false, "C:\\ended", "", "", T);
        Check(CodexPetAdapter.Map([Session(K15NormalizedState.Normal), ended], new Dictionary<string, CodexUnreadState>()).State == CodexPetVisualState.Idle, "PET_MULTI_NORMAL_ENDED_IDLE");
        var snapshotReader = new Reader { Ids = ["TB"] };
        var unreadSnapshot = snapshotReader.Read(T);
        Check(snapshotReader.Calls == 1 && CodexPetAdapter.Map([doneA, doneB], unreadSnapshot).State == CodexPetVisualState.Review,
            "PET_MULTI_DONE_ONE_SHARED_UNREAD_SNAPSHOT");
        Check(CodexPetAdapter.ShouldPollUnread([Session(K15NormalizedState.DonePendingAttention)]), "PET_DONE_POLLING_STAYS_ON");
        Check(CodexPetAdapter.ShouldPollUnread([doneA, doneB]), "PET_MULTI_DONE_POLLING_STAYS_ON");
        Check(CodexPetAdapter.ShouldPollUnread([Session(K15NormalizedState.DonePendingAttention)]) &&
            CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.Unknown).State == CodexPetVisualState.Review &&
            CodexPetAdapter.Map([Session(K15NormalizedState.DonePendingAttention)], "S", CodexUnreadState.HasUnread).State == CodexPetVisualState.Review,
            "PET_DONE_PRESENTATION_WAITS_FOR_REDUCER_ACK");
        Check(!CodexPetAdapter.ShouldPollUnread([Session(K15NormalizedState.Normal)]), "PET_POLLING_STOPS_AFTER_DONE");
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
        reducer.Apply(ApprovalRequest(5, "T", "U2", "I2", "92"));
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
        reducer.Apply(Hook("UserPromptSubmit", 3, "B", "TB", "UB"));
        reducer.Apply(ApprovalRequest(4, "TB", "UB", "IB", "33"));
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
            "codex_stop", T.AddSeconds(2), "T", "U", "", "", false, SourceInstanceId: TestSourceInstanceId);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_MISSING_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted with { TimestampUtc = T }]);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_STALE_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted]);
        Check(reducer.ReadAckCandidates.Single().ThreadId == "T", "REHYDRATION_EXACT_PERSISTED_CORRELATION");
        reducer.RestoreCompletionCorrelations([persisted, persisted with { ThreadId = "OTHER" }]);
        Check(reducer.ReadAckCandidates.Count == 0, "REHYDRATION_AMBIGUOUS_CORRELATION");
        Check(JournalStateNormalizer.ParseInput("{\"source\":\"state_normalizer\",\"event\":\"read_ack_evidence\"}") is null,
            "ACK_DIAGNOSTIC_NEVER_REPLAYED_AS_INPUT");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        var durableCompletion = reducer.ReadAckCandidates.Single();
        var durableAck = new CodexReadAckCheckpoint(
            durableCompletion.SourceInstanceId,
            durableCompletion.SessionId,
            durableCompletion.ThreadId,
            durableCompletion.TurnId,
            durableCompletion.Generation,
            durableCompletion.CompletedUtc,
            T.AddSeconds(9));
        reducer.RestoreReadAcknowledgments([durableAck]);
        Check(reducer.State == K15NormalizedState.Normal &&
              reducer.SessionSnapshots.Single().State == K15NormalizedState.Normal &&
              reducer.LastSessionTransitions.Any(transition =>
                  transition.Reason == "codex_read_ack" && transition.IsRehydrated),
            "DURABLE_ACK_EXACT_REHYDRATION_RESTORES_NORMAL");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        reducer.RestoreReadAcknowledgments([durableAck with
        {
            SourceInstanceId = "local:00000000000000000000000000000000"
        }]);
        Check(reducer.State == K15NormalizedState.DonePendingAttention,
            "DURABLE_ACK_WRONG_SOURCE_FAILS_CLOSED");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        reducer.RestoreReadAcknowledgments([durableAck with { Generation = durableAck.Generation + 1 }]);
        Check(reducer.State == K15NormalizedState.DonePendingAttention,
            "DURABLE_ACK_WRONG_GENERATION_FAILS_CLOSED");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        reducer.RestoreReadAcknowledgments([durableAck, durableAck]);
        Check(reducer.State == K15NormalizedState.DonePendingAttention,
            "DURABLE_ACK_DUPLICATE_FAILS_CLOSED");

        reducer = new StateReducer(0, T.AddSeconds(10));
        reducer.Rehydrate(replay);
        reducer.RestoreReadAcknowledgments([durableAck with { SourceInstanceId = string.Empty }]);
        Check(reducer.State == K15NormalizedState.Normal,
            "DURABLE_ACK_LEGACY_SOURCELESS_UNIQUE_COMPLETION_RESTORES");
        reducer.Apply(Hook("UserPromptSubmit", 11, turn: "U2"));
        Check(reducer.State == K15NormalizedState.Running,
            "DURABLE_ACK_OLD_CHECKPOINT_DOES_NOT_SUPPRESS_NEW_TURN");

        var checkpointJson =
            "{\"timestampUtc\":\"2026-08-25T00:00:05.1000000Z\",\"source\":\"state_normalizer\",\"event\":\"read_ack_evidence\",\"reason\":\"codex_read_ack\",\"host\":\"local\",\"sessionId\":\"S\",\"threadId\":\"T\",\"turnId\":\"U\",\"sourceInstanceId\":\"" +
            TestSourceInstanceId +
            "\",\"runtimeEpoch\":\"11111111-1111-1111-1111-111111111111\",\"completionGeneration\":1,\"completedUtc\":\"2026-08-25T00:00:02Z\",\"hasUnreadUtc\":\"2026-08-25T00:00:03Z\",\"firstNoUnreadUtc\":\"2026-08-25T00:00:04Z\",\"secondNoUnreadUtc\":\"2026-08-25T00:00:05Z\"}";
        var parsedCheckpoint = JournalStateNormalizer.ParseReadAckCheckpoint(checkpointJson);
        Check(parsedCheckpoint?.SourceInstanceId == TestSourceInstanceId &&
              parsedCheckpoint.SessionId == "S" && parsedCheckpoint.ThreadId == "T" &&
              parsedCheckpoint.TurnId == "U" && parsedCheckpoint.Generation == 1,
            "DURABLE_ACK_CHECKPOINT_PARSER_EXACT");
        Check(JournalStateNormalizer.ParseReadAckCheckpoint(checkpointJson.Replace(
                "\"secondNoUnreadUtc\":\"2026-08-25T00:00:05Z\"",
                "\"secondNoUnreadUtc\":\"2026-08-25T00:00:05Z\",\"prompt\":\"PRIVATE\"")) is null,
            "DURABLE_ACK_CHECKPOINT_REJECTS_UNEXPECTED_FIELD");
        Check(JournalStateNormalizer.ParseReadAckCheckpoint(checkpointJson.Replace(
                "\"threadId\":\"T\"",
                "\"threadId\":\"T\",\"threadId\":\"OTHER\"")) is null,
            "DURABLE_ACK_CHECKPOINT_REJECTS_DUPLICATE_FIELD");

        FileReaderTests();
        ArchitectRegressions();
        NormalizerTests();
        Console.WriteLine($"READ_ACK_SCENARIOS_PASSED={_passed}");
    }

    private static void NormalizerLivenessTests()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "k15-liveness-normalizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        EventJournal.SetTestDirectoryPath(directory);
        JournalStateNormalizer? normalizer = null;
        try
        {
            EventJournal.EnsureExists();
            var provider = new MutableLiveness { State = CodexLivenessState.NotRunning };
            var now = DateTimeOffset.UtcNow;

            void AppendPrompt(string session, string thread, string turn)
            {
                var line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "codex_hook",
                    @event = "UserPromptSubmit",
                    sessionId = session,
                    threadId = thread,
                    turnId = turn,
                    cwd = @"C:\synthetic"
                });
                File.AppendAllText(EventJournal.FilePath, line + Environment.NewLine);
            }

            // Historical replay says RUNNING, but authoritative desktop
            // liveness says the runtime is already gone.
            File.AppendAllText(EventJournal.FilePath, JsonSerializer.Serialize(new
            {
                timestampUtc = now.AddSeconds(-2),
                source = "codex_hook",
                @event = "UserPromptSubmit",
                sessionId = "startup-ghost",
                threadId = "startup-thread",
                turnId = "startup-turn",
                cwd = @"C:\synthetic"
            }) + Environment.NewLine);

            normalizer = new JournalStateNormalizer(0, unreadSources: null, livenessProvider: provider);
            normalizer.Start();

            Check(provider.Calls >= 1 &&
                  normalizer.State == K15NormalizedState.Normal &&
                  normalizer.SessionSnapshots.Count == 0 &&
                  normalizer.AttentionSnapshot.ActiveTaskSessionCount == 0,
                "NORMALIZER_STARTUP_DEAD_DESKTOP_CLEARS_REHYDRATED_GHOST");

            // A delayed hook from the dead runtime must not get through the
            // 400 ms reorder buffer after the liveness boundary.
            AppendPrompt("late-ghost", "late-thread", "late-turn");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && provider.Calls < 2)
                Thread.Sleep(30);
            Thread.Sleep(650);
            Check(normalizer.State == K15NormalizedState.Normal &&
                  normalizer.SessionSnapshots.Count == 0,
                "NORMALIZER_DEAD_DESKTOP_DROPS_PENDING_OLD_RUNTIME_EVENT");

            // A real new desktop start must not lose its first hook merely
            // because the previous observed state was NotRunning.
            provider.State = CodexLivenessState.Alive;
            AppendPrompt("new-runtime", "new-thread", "new-turn");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline &&
                   (normalizer.State != K15NormalizedState.Running ||
                    normalizer.SessionSnapshots.All(row => row.SessionId != "new-runtime")))
            {
                Thread.Sleep(30);
            }
            Check(normalizer.State == K15NormalizedState.Running &&
                  normalizer.SessionSnapshots.Count == 1 &&
                  normalizer.SessionSnapshots[0].SessionId == "new-runtime",
                "NORMALIZER_NEW_DESKTOP_FIRST_EVENT_SURVIVES_PRIOR_NOTRUNNING");

            provider.State = CodexLivenessState.NotRunning;
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline &&
                   (normalizer.State != K15NormalizedState.Normal ||
                    normalizer.SessionSnapshots.Count != 0))
            {
                Thread.Sleep(30);
            }
            Check(normalizer.State == K15NormalizedState.Normal &&
                  normalizer.SessionSnapshots.Count == 0 &&
                  normalizer.AttentionSnapshot.ActiveTaskSessionCount == 0,
                "NORMALIZER_RUNTIME_EXIT_CLEARS_LIVE_LEDGER_WITHOUT_RESTART");

            var journal = File.ReadAllText(EventJournal.FilePath);
            Check(journal.Contains("runtime_liveness_reconciled", StringComparison.Ordinal) &&
                  journal.Contains("codex_desktop_not_running", StringComparison.Ordinal) &&
                  journal.Contains("\"clearedSessionCount\":1", StringComparison.Ordinal),
                "NORMALIZER_DESKTOP_EXIT_REASON_PERSISTED");
        }
        finally
        {
            if (normalizer is not null)
                normalizer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            EventJournal.SetTestDirectoryPath(null);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static void SourceIdentityTests()
    {
        var homeA = Path.Combine(Path.GetTempPath(), "codex-source-a-" + Guid.NewGuid().ToString("N"));
        var homeB = Path.Combine(Path.GetTempPath(), "codex-source-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeA);
        Directory.CreateDirectory(homeB);
        try
        {
            var sourceA = CodexSourceIdentity.ForHome(homeA)!;
            var sourceB = CodexSourceIdentity.ForHome(homeB)!;
            Check(CodexSourceIdentity.IsValid(sourceA) && CodexSourceIdentity.IsValid(sourceB) && sourceA != sourceB,
                "SOURCE_IDS_DISTINCT_AND_BOUNDED");
            Check(sourceA == CodexSourceIdentity.ForHome(homeA.ToLowerInvariant()) &&
                  sourceA == CodexSourceIdentity.ForHome(homeA.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "SOURCE_ID_STABLE_NORMALIZATION");

            File.WriteAllText(Path.Combine(homeA, ".codex-global-state.json"), Store("{\"local\":[\"T\"]}"));
            File.WriteAllText(Path.Combine(homeB, ".codex-global-state.json"), Store("{\"local\":[]}"));
            var registry = new CodexUnreadSourceRegistry(new[] { homeA, homeB });
            Check(registry.SourceCount == 2 && registry.Read(sourceA, T).ForThread("T") == CodexUnreadState.HasUnread &&
                  registry.Read(sourceB, T).ForThread("T") == CodexUnreadState.NoUnread,
                "UNREAD_SOURCES_STAY_ISOLATED");
            Check(registry.Read("", T).Failure == CodexUnreadState.Unknown,
                "LEGACY_MULTI_HOME_SOURCE_FAILS_CLOSED");

            var reducer = new StateReducer(0, T);
            reducer.Apply(Hook("UserPromptSubmit", 1, sourceInstanceId: sourceA));
            reducer.Apply(Hook("UserPromptSubmit", 1, sourceInstanceId: sourceB));
            reducer.Apply(Hook("Stop", 2, sourceInstanceId: sourceA));
            reducer.Apply(Hook("Stop", 2, sourceInstanceId: sourceB));
            Check(reducer.SessionSnapshots.Count == 2 && reducer.SessionSnapshots.All(session => session.SessionId == "S") &&
                  reducer.ReadAckCandidates.Count == 2 &&
                  reducer.ReadAckCandidates.Select(key => key.SourceInstanceId).Distinct().Count() == 2,
                "SESSION_AND_THREAD_IDENTITIES_PARTITION_BY_SOURCE");

            var rows = CodexActivityNormalizer.Normalize(reducer.SessionSnapshots,
                new Dictionary<string, CodexUnreadState>
                {
                    [CodexSourceIdentity.CompositeKey(sourceA, "T")] = CodexUnreadState.HasUnread,
                    [CodexSourceIdentity.CompositeKey(sourceB, "T")] = CodexUnreadState.NoUnread
                });
            Check(rows.Count == 2 && rows.Select(row => row.IdentityKey).Distinct().Count() == 2 &&
                  rows.Single(row => row.SourceInstanceId == sourceA).Unread == CodexUnreadState.HasUnread &&
                  rows.Single(row => row.SourceInstanceId == sourceB).Unread == CodexUnreadState.NoUnread,
                "ACTIVITY_ROWS_PARTITION_BY_SOURCE");

            File.WriteAllText(Path.Combine(homeB, ".codex-global-state.json"), "{malformed");
            Check(registry.Read(sourceA, T).ForThread("T") == CodexUnreadState.HasUnread &&
                  registry.Read(sourceB, T).Failure == CodexUnreadState.Unknown,
                "ONE_MALFORMED_SOURCE_DOES_NOT_BREAK_OTHER");
        }
        finally
        {
            if (Directory.Exists(homeA)) Directory.Delete(homeA, true);
            if (Directory.Exists(homeB)) Directory.Delete(homeB, true);
        }
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

            var restarted = new JournalStateNormalizer(0, reader);
            try
            {
                restarted.Start();
                Check(restarted.AttentionSnapshot.DoneUnreadCount == 1 &&
                      restarted.State == K15NormalizedState.DonePendingAttention &&
                      restarted.SessionSnapshots.Single(s => s.SessionId == "S").State == K15NormalizedState.Normal &&
                      restarted.SessionSnapshots.Single(s => s.SessionId == "B").State == K15NormalizedState.DonePendingAttention,
                    "NORMALIZER_RESTART_DURABLE_ACK_PREVENTS_RESURRECTION");
            }
            finally { restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

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
