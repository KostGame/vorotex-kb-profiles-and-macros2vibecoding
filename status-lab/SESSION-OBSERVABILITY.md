# K15 session observability contract

`state_normalizer/session_state_changed` is the per-session evidence plane. It
contains `sessionId`, `previous`, `current`, `reason`, `sourceTimestampUtc`,
`isRehydrated`, and bounded opaque `correlation` fields (`threadId`, `turnId`,
`rpcIdType`, `rpcId`). It never contains prompt text, model output, tool
arguments, command contents, raw protocol, credentials, or reusable secrets.

For the required future same-session live approval verification, the architect
must correlate these records:

1. `codex_stdio_bridge/approval_requested` with exact thread, turn, and item IDs;
2. `state_normalizer/session_state_changed` for the uniquely correlated session with
   `sessionId=S`, `current=WAITING`, `reason=codex_permission_request`,
   `isRehydrated=false`;
3. the matching `codex_stdio_bridge/approval_resolved` record;
4. `state_normalizer/session_state_changed` with `sessionId=S`,
   `previous=WAITING`, `current=RUNNING`, `reason=codex_approval_resolved`,
   `isRehydrated=false`;
5. `codex_hook/PostToolUse` for the same session S, after that transition.

The raw `PermissionRequest` alone is diagnostic/lifecycle evidence and does
not enter WAITING. WAITING authority comes from a sanitized
`codex_stdio_bridge/approval_requested` record with schema
`k15-codex-approval-request/v1`, emitted for command execution, file change,
and permissions requests only after the effective reviewer for that exact turn
is proven `user` or `auto_review`. It must include bounded `rpcIdType`,
`rpcId`, `requestFamily`, `approvalsReviewer`, `threadId`, `turnId`,
`itemId`, and `sourceInstanceId`; private request payload fields are never
persisted. The reducer independently requires `approvalsReviewer=user` and a
unique active non-internal session matching the exact source instance, thread,
and turn. Missing or ambiguous matches fail closed. `auto_review`, unknown,
or missing reviewer provenance never enters WAITING. Reviewer state comes from
matching thread start/resume/fork responses, settings updates, and exact
turn/start overrides; turn overrides are removed on turn completion.

For command/file approvals, an accepted `approval_resolved` record can move
the exact WAITING session to RUNNING when its typed RPC ID and item match the
request. Decline and cancel remain decisions without resume authority. The
permissions family has different response semantics:
`permissions_approval_observed` remains diagnostic-only and never claims
RUNNING. Live Codex Desktop evidence shows that
`PreToolUse(toolName=request_permissions)` is emitted while the native owner
approval card is still open, so that exact same-turn hook is lifecycle/activity
evidence only and must preserve WAITING. `PostToolUse(request_permissions)`,
or a later exact same-turn `PreToolUse` for another tool, remains resume
authority, while existing Stop/completion evidence remains terminal authority.

For Issue #93, `codex_stdio_bridge/turn_completed` with schema
`k15-codex-completion/v1` and status `completed` is the candidate authoritative
no-Stop completion signal. The production bridge observes only bounded opaque
`threadId`, `turnId`, and terminal `status`; turn items, messages, errors,
usage, prompts, tools, commands, paths, and raw protocol are never persisted.
The reducer requires exact thread and turn correlation to exactly one active
non-internal session. It emits that session's
`session_state_changed` with `reason=codex_turn_completed`; aggregate state is
separate and is not proof that an individual task completed.
Only a matching `RUNNING` session is eligible for the first completion
transition. A repeated completion for an already DONE session is idempotent;
NORMAL (including explicitly acknowledged) and WAITING sessions, ended
sessions, ambiguous matches, and wrong turns fail closed. This authority never
writes `LastStopUtc`: only a real `codex_hook/Stop` may create Stop evidence.

`SessionEnd` remains lifecycle-only evidence and is never completion authority.
The planned owner live verifier chain is:

`UserPromptSubmit(S,T) -> RUNNING -> sanitized turn_completed(threadId,turnId=T,status=completed) -> same S DONE_PENDING_ATTENTION`.

The no-Stop canary must prove that `Stop` is absent for the same turn; a later
`SessionEnd` must not be treated as the DONE authority. Final acceptance still
requires an owner live canary against the current Codex Desktop/runtime.

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
