# K15 session observability contract

`state_normalizer/session_state_changed` is the per-session evidence plane. It
contains `sessionId`, `previous`, `current`, `reason`, `sourceTimestampUtc`,
`isRehydrated`, and bounded opaque `correlation` fields (`threadId`, `turnId`,
`rpcIdType`, `rpcId`). It never contains prompt text, model output, tool
arguments, command contents, raw protocol, credentials, or reusable secrets.

For live approval verification, the architect must correlate these records:

1. `codex_hook/PermissionRequest` for session S;
2. `state_normalizer/session_state_changed` for the same session with
   `sessionId=S`, `current=WAITING`, `reason=codex_permission_request`,
   `isRehydrated=false`;
3. the correlated `codex_stdio_bridge/approval_resolved` record;
4. `state_normalizer/session_state_changed` with `sessionId=S`,
   `previous=WAITING`, `current=RUNNING`, `reason=codex_approval_resolved`,
   `isRehydrated=false`;
5. `codex_hook/PostToolUse` for the same session S, after that transition.

The raw `PermissionRequest` alone is not session WAITING evidence; the
per-session WAITING transition is required.

`state_normalizer/normalized_state_changed` remains the backward-compatible
aggregate plane. It has `plane=aggregate`, aggregate previous/current values,
focused session, counts, `driverSessionId`, and `driverReason`. Aggregate
`WAITING -> RUNNING` is not required as proof that a particular session resumed.
An ended session may remain the `DONE_PENDING_ATTENTION` driver because ended
DONE_UNREAD contributes to that aggregate; WAITING and RUNNING drivers are
always active sessions.

`JournalStateNormalizer.SessionSnapshots` exposes each non-internal session's
opaque id, state, liveness, and focus. `AttentionSnapshot` exposes aggregate
state, running/waiting/done counts, and the effective aggregate driver.
