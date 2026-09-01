# K15 DONE R5 offline canary diagnostic

This package prepares and validates the Issue #93 R5 diagnostic without
starting Codex Desktop, `codex app-server`, or the owner live canary. It
accepts already-sanitized hook and `k15-codex-completion/v1` bridge events plus
the real Status Lab `state_normalizer/session_state_changed` per-session shape.
It reports production state only when that real transition is present; hook
and completion inputs alone are never synthetic DONE evidence.

Results distinguish `NO_STOP_LIVE_DONE_ACCEPTED`, `STOP_AUTHORED_DONE`,
`COMPLETION_PRESENT_BUT_NO_PRODUCTION_DONE`, `CORRELATION_FIX_CANDIDATE`,
identity/ambiguity failures, non-success statuses, and rehydrated DONE. The
future fallback is reported only when the observed session thread is empty,
the completion thread equals the session ID, the turn matches, and no live
production DONE transition exists.

It deliberately does not alter `StateReducer`, hook installation, the
production bridge activation path, `CODEX_CLI_PATH`, WindowsApps, or any
device behavior. The future live run must use the accepted production
activation kit at `../codex-stdio-bridge/production/Activate-CodexBridge.ps1`.

Run the deterministic suite with:

```text
npm.cmd test
```

Persisted evidence is JSONL and contains only the allowlisted fields in the
R5 contract. Nested correlation and `isRehydrated` are inspected transiently;
prompt text, model output, commands, tools, paths, raw protocol, credentials,
and tokens are discarded before persistence.
