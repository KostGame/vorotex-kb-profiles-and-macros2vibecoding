# K15 DONE R5 offline canary diagnostic

This package prepares and validates the Issue #93 R5 diagnostic without
starting Codex Desktop, `codex app-server`, or the owner live canary. It
accepts only the already-sanitized hook and `k15-codex-completion/v1` bridge
events, deduplicates replayed records, records chronology, and reports the
empty `session.ThreadId` condition as `candidate_session_id_thread_id_mismatch`.

It deliberately does not alter `StateReducer`, hook installation, the
production bridge activation path, `CODEX_CLI_PATH`, WindowsApps, or any
device behavior. The future live run must use the accepted production
activation kit at `../codex-stdio-bridge/production/Activate-CodexBridge.ps1`.

Run the deterministic suite with:

```text
npm.cmd test
```

Persisted evidence is JSONL and contains only the allowlisted fields in the
R5 contract. Prompt text, model output, commands, tools, paths, raw protocol,
credentials, and tokens are discarded before persistence.
