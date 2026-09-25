using Vorotex.K15.StatusLab;

internal static class CodexLocalThreadTitleTests
{
    internal static void Run()
    {
        var response = """
            {"jsonrpc":"2.0","id":2,"result":{"thread":{"id":"thread-exact","name":"Release notes","preview":"PRIVATE_PROMPT_MARKER","cwd":"C:/repo","model":"PRIVATE_MODEL_MARKER","turns":[{"items":[{"type":"tool","text":"PRIVATE_TOOL_MARKER"}]}]}}}
            """;
        var exactName = CodexThreadReadTitleParser.Parse(response, "thread-exact");
        Check(exactName == "Release notes", "EXACT_THREAD_ID_TITLE_MATCH");
        Check(!new[] { "PRIVATE_PROMPT_MARKER", "PRIVATE_MODEL_MARKER", "PRIVATE_TOOL_MARKER" }
            .Any(secret => exactName!.Contains(secret, StringComparison.Ordinal)), "THREAD_CONTENT_DOES_NOT_ESCAPE_PARSER");

        Check(CodexThreadReadTitleParser.Parse(response, "thread-other") is null, "MISMATCHED_THREAD_ID_REJECTED");
        Check(CodexThreadReadTitleParser.Parse(
            "{\"result\":{\"thread\":{\"id\":\"thread-exact\",\"name\":\"  \"}}}", "thread-exact") is null,
            "EMPTY_TITLE_REJECTED");
        Check(CodexThreadReadTitleParser.Parse(
            "{\"result\":{\"thread\":{\"id\":\"thread-exact\",\"name\":\"bad\\nname\"}}}", "thread-exact") is null,
            "CONTROL_TITLE_REJECTED");

        var sessions = new[]
        {
            new CodexSessionSnapshot("session-run", K15NormalizedState.Running, true, true,
                Cwd: "C:/secondary-context", ThreadId: "thread-run"),
            new CodexSessionSnapshot("session-done", K15NormalizedState.DonePendingAttention, false, false,
                ThreadId: "thread-done")
        };
        var unread = new Dictionary<string, CodexUnreadState>(StringComparer.Ordinal)
        {
            ["thread-done"] = CodexUnreadState.HasUnread
        };
        var baseline = CodexActivityNormalizer.Normalize(sessions, unread);
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["thread-run"] = exactName!,
            // A mismatched provider result cannot label this row.
            ["thread-other"] = "Wrong task"
        };
        var enriched = CodexActivityNormalizer.EnrichLocalTitles(baseline, names);
        Check(enriched.Single(row => row.ThreadId == "thread-run").Title == "Release notes" &&
              enriched.Single(row => row.ThreadId == "thread-done").Title == "Codex task session-" &&
              enriched.Single(row => row.ThreadId == "thread-run").Cwd == "C:/secondary-context" &&
              enriched.Single(row => row.ThreadId == "thread-run").Title != "C:/secondary-context",
            "MISSING_OR_MISMATCHED_TITLE_USES_STABLE_FALLBACK");

        var remoteRow = new CodexActivityRow(CodexActivitySource.Remote, "remote-ssh-discovered:test",
            "remote-session", "thread-run", null, "Remote title", null, null, null,
            CodexActivityState.Running, CodexUnreadState.Unknown, DateTimeOffset.UtcNow,
            baseline[0].Evidence with { Source = CodexActivitySource.Remote }, CodexActivityConfidence.Trusted,
            CodexActivityOpenTarget.Unavailable(CodexActivitySource.Remote, "thread-run", null, "unavailable"));
        var localAndRemote = CodexActivityNormalizer.EnrichLocalTitles([baseline[0], remoteRow], names);
        Check(localAndRemote.Single(row => row.Source == CodexActivitySource.Remote).Title == "Remote title",
            "LOCAL_METADATA_MAKES_NO_REMOTE_TRUST_CLAIM");

        var failedProviderPresentation = CodexPetAdapter.MapPresentation(sessions, unread,
            new Dictionary<string, string>(StringComparer.Ordinal));
        var successfulProviderPresentation = CodexPetAdapter.MapPresentation(sessions, unread, names);
        Check(failedProviderPresentation.RelevantTaskCount == successfulProviderPresentation.RelevantTaskCount &&
              failedProviderPresentation.Global == successfulProviderPresentation.Global &&
              failedProviderPresentation.Tasks.Select(task => (task.Activity.State, task.Activity.Unread))
                  .SequenceEqual(successfulProviderPresentation.Tasks.Select(task =>
                      (task.Activity.State, task.Activity.Unread))) &&
              failedProviderPresentation.Tasks.Single(task => task.ThreadId == "thread-done").Activity.Unread ==
                  CodexUnreadState.HasUnread,
            "PROVIDER_FAILURE_PRESERVES_STATE_COUNT_AND_UNREAD");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine(name + "=PASS");
    }
}
