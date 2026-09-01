# R5 LIVE runner (owner-controlled)

This package prepares and verifies the owner canary; implementation and tests never run the live canary. Run the numbered `.cmd` launcher for exactly one phase. `PREPARE` is read-only against Codex configuration. Before `ARM`, close Codex Desktop completely. After ARM, perform exactly one harmless ordinary turn manually, close Codex, then run `VERIFY-DISABLE`. `ROLLBACK` is safe after a partial ARM.

The runner reuses `production/Activate-CodexBridge.ps1` and imports `src/r5-diagnostic.mjs`. It records only the allowlisted sanitized chronology. It never stores prompts, responses, commands, tool arguments, raw JSON-RPC, credentials, or raw journal deltas. Owner-local state is written under `%LOCALAPPDATA%\VorotexK15\app\codex-done-r5-live` and is not repository evidence.

Suggested harmless turn: `Ответь одной строкой: R5 CANARY OK. Не используй инструменты и не изменяй файлы.` A `Stop` event is diagnosed as `STOP_AUTHORED_DONE`; it is not Issue #93 acceptance.

`VERIFY_DISABLE` requires relevant processes to be closed, bounds the journal delta to 1 MiB, diagnoses only selected events, disables the bridge, restores exact User environment presence/value state, and reports rollback failures loudly. It never changes Machine environment, WindowsApps, hooks, or Git worktrees.
