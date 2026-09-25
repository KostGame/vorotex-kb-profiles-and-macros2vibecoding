using Vorotex.K15.StatusLab;

internal static class CodexLocalThreadTitleTests
{
    internal static void Run()
    {
        var responseA = """
            {"jsonrpc":"2.0","id":2,"result":{"thread":{"id":"thread-shared","name":"Source A title","preview":"PRIVATE_PROMPT_MARKER","cwd":"C:/repo-a","model":"PRIVATE_MODEL_MARKER","turns":[{"items":[{"type":"tool","text":"PRIVATE_TOOL_MARKER"}]}]}}}
            """;
        var responseB = """
            {"jsonrpc":"2.0","id":2,"result":{"thread":{"id":"thread-shared","name":"Source B title","preview":"PRIVATE_PROMPT_MARKER_B"}}}
            """;
        var exactNameA = CodexThreadReadTitleParser.Parse(responseA, "thread-shared");
        var exactNameB = CodexThreadReadTitleParser.Parse(responseB, "thread-shared");
        Check(exactNameA == "Source A title" && exactNameB == "Source B title",
            "SAME_THREAD_ID_READ_WITH_SOURCE_LOCAL_TITLES");
        Check(!new[] { "PRIVATE_PROMPT_MARKER", "PRIVATE_PROMPT_MARKER_B", "PRIVATE_MODEL_MARKER", "PRIVATE_TOOL_MARKER" }
            .Any(secret => exactNameA!.Contains(secret, StringComparison.Ordinal)), "THREAD_CONTENT_DOES_NOT_ESCAPE_PARSER");
        Check(CodexThreadReadTitleParser.Parse(responseA, "thread-other") is null, "MISMATCHED_THREAD_ID_REJECTED");
        Check(CodexThreadReadTitleParser.Parse(
            "{\"result\":{\"thread\":{\"id\":\"thread-shared\",\"name\":\"  \"}}}", "thread-shared") is null,
            "EMPTY_TITLE_REJECTED");
        Check(CodexThreadReadTitleParser.Parse(
            "{\"result\":{\"thread\":{\"id\":\"thread-shared\",\"name\":\"bad\\nname\"}}}", "thread-shared") is null,
            "CONTROL_TITLE_REJECTED");

        var homeA = Path.Combine(Path.GetTempPath(), "codex-title-a-" + Guid.NewGuid().ToString("N"));
        var homeB = Path.Combine(Path.GetTempPath(), "codex-title-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeA);
        Directory.CreateDirectory(homeB);
        try
        {
            var sourceA = CodexSourceIdentity.ForHome(homeA)!;
            var sourceB = CodexSourceIdentity.ForHome(homeB)!;
            var registeredA = Source(sourceA, homeA);
            var registeredB = Source(sourceB, homeB);
            var sessions = new[]
            {
                new CodexSessionSnapshot("session-a", K15NormalizedState.Running, true, true,
                    Cwd: "C:/secondary-context-a", ThreadId: "thread-shared", SourceInstanceId: sourceA),
                new CodexSessionSnapshot("session-b", K15NormalizedState.Running, true, false,
                    Cwd: "C:/secondary-context-b", ThreadId: "thread-shared", SourceInstanceId: sourceB),
                new CodexSessionSnapshot("session-done", K15NormalizedState.DonePendingAttention, false, false,
                    ThreadId: "thread-done", SourceInstanceId: sourceA),
                new CodexSessionSnapshot("session-missing", K15NormalizedState.Running, true, false,
                    ThreadId: "thread-shared"),
                new CodexSessionSnapshot("session-unknown", K15NormalizedState.Running, true, false,
                    ThreadId: "thread-shared", SourceInstanceId: CodexSourceIdentity.ForHome("C:/unknown-home")!)
            };
            var unread = new Dictionary<string, CodexUnreadState>(StringComparer.Ordinal)
            {
                [CodexSourceIdentity.CompositeKey(sourceA, "thread-done")] = CodexUnreadState.HasUnread
            };

            var requests = CodexLocalThreadTitleSourceResolver.Resolve(sessions, [registeredA, registeredB]);
            Check(requests.Count == 2 &&
                  requests.Single(request => request.SourceInstanceId == sourceA).CodexHomePath ==
                      CodexSourceIdentity.CanonicalizeHome(homeA) &&
                  requests.Single(request => request.SourceInstanceId == sourceB).CodexHomePath ==
                      CodexSourceIdentity.CanonicalizeHome(homeB),
                "SOURCE_RESOLVER_BINDS_EACH_ID_TO_EXACT_CODEX_HOME");
            Check(CodexLocalThreadTitleSourceResolver.Resolve(sessions, [registeredA, registeredA])
                    .All(request => request.SourceInstanceId != sourceA) &&
                  CodexLocalThreadTitleSourceResolver.Resolve(sessions, [registeredA])
                    .All(request => request.SourceInstanceId != sourceB) &&
                  CodexLocalThreadTitleSourceResolver.Resolve(sessions, Array.Empty<CodexUnreadSource>()).Count == 0,
                "MISSING_UNKNOWN_OR_DUPLICATE_SOURCE_FAILS_CLOSED");

            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CodexSourceIdentity.CompositeKey(sourceA, "thread-shared")] = exactNameA!,
                [CodexSourceIdentity.CompositeKey(sourceB, "thread-shared")] = exactNameB!
            };
            var rows = CodexActivityNormalizer.Normalize(sessions, unread);
            var enriched = CodexActivityNormalizer.EnrichLocalTitles(rows, names);
            Check(enriched.Single(row => row.SessionId == "session-a").Title == "Source A title" &&
                  enriched.Single(row => row.SessionId == "session-b").Title == "Source B title",
                "SAME_THREAD_ID_DIFFERENT_SOURCE_TITLES_NO_COLLISION");

            var onlyA = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CodexSourceIdentity.CompositeKey(sourceA, "thread-shared")] = exactNameA!
            };
            var isolated = CodexActivityNormalizer.EnrichLocalTitles(rows, onlyA);
            Check(isolated.Single(row => row.SessionId == "session-a").Title == "Source A title" &&
                  isolated.Single(row => row.SessionId == "session-b").Title == "Codex task session-" &&
                  isolated.Single(row => row.SessionId == "session-missing").Title == "Codex task session-" &&
                  isolated.Single(row => row.SessionId == "session-unknown").Title == "Codex task session-",
                "SOURCE_A_TITLE_CANNOT_ENRICH_SOURCE_B_OR_UNBOUND_ROW");
            Check(enriched.Single(row => row.SessionId == "session-a").Cwd == "C:/secondary-context-a" &&
                  enriched.Single(row => row.SessionId == "session-a").Title != "C:/secondary-context-a" &&
                  enriched.Single(row => row.SessionId == "session-a").OpenTarget.CanFocus == false,
                "CWD_REMAINS_SECONDARY_CONTEXT");

            var failedProvider = CodexPetAdapter.MapPresentation(sessions, unread,
                new Dictionary<string, string>(StringComparer.Ordinal));
            var successfulProvider = CodexPetAdapter.MapPresentation(sessions, unread, names);
            Check(failedProvider.RelevantTaskCount == successfulProvider.RelevantTaskCount &&
                  failedProvider.Global == successfulProvider.Global &&
                  failedProvider.Tasks.Select(task => (task.Activity.State, task.Activity.Unread))
                      .SequenceEqual(successfulProvider.Tasks.Select(task => (task.Activity.State, task.Activity.Unread))) &&
                  failedProvider.Tasks.Single(task => task.ThreadId == "thread-done").Activity.Unread ==
                      CodexUnreadState.HasUnread,
                "PROVIDER_FAILURE_PRESERVES_STATE_COUNT_AND_UNREAD");
        }
        finally
        {
            Directory.Delete(homeA, recursive: true);
            Directory.Delete(homeB, recursive: true);
        }
    }

    private static CodexUnreadSource Source(string sourceInstanceId, string homePath) =>
        new(sourceInstanceId, CodexSourceIdentity.CanonicalizeHome(homePath)!, null, "local",
            CodexUnreadSourceHealth.Unavailable, "test source");

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine(name + "=PASS");
    }
}
