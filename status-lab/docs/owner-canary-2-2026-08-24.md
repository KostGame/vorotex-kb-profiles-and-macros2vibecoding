# Status Lab owner canary 2 — 2026-08-24

This note records sanitized findings from the second Windows owner canary. Raw local journals, notification text, machine-specific paths, session IDs and turn IDs are intentionally not committed.

## Result

```text
WINDOWS_NOTIFICATION_ACCESS = PASS
WINDOWS_NOTIFICATION_POLL = PASS
CODEX_HOME_TARGET = PASS
CODEX_HOOK_USER_PROMPT_SUBMIT = PASS
CODEX_HOOK_PERMISSION_REQUEST = PASS
CODEX_HOOK_STOP = PASS
CODEX_HOOK_SESSION_END = NOT_EXERCISED
CODEX_AND_WINDOWS_CORRELATION = PASS
```

The active hooks file was the AgentLoop-oriented Codex home rather than the generic `.codex` home.

## Hook observations

The sanitized journal contained:

```text
UserPromptSubmit  = 4
PermissionRequest = 4
Stop              = 4
SessionEnd        = 0  # session was not closed during the canary
```

All observed Codex hook records belonged to one live Codex session and carried stable model/session/turn metadata. Permission requests exposed `toolName = Bash` without persisting tool input.

One turn emitted two `PermissionRequest` events before `Stop`. The future normalizer must therefore treat repeated permission events as idempotent `WAITING` transitions rather than as distinct user-attention states.

## Notification correlation

For the tested OpenAI desktop package, new notifications were observed both:

1. after `PermissionRequest` and before `Stop`;
2. immediately after `Stop`.

This gives a useful timing-based distinction even though ChatGPT and Codex share the same Windows package identity:

```text
PermissionRequest
  -> OpenAI notification added before Stop
  -> WAITING / attention request

Stop
  -> OpenAI notification added immediately after Stop
  -> DONE / completion attention
```

The notification text heuristic is not authoritative. A post-`Stop` notification can contain words that also match the permission heuristic. State attribution should therefore prefer lifecycle timing over text classification.

## Proposed normalizer precedence

```text
UserPromptSubmit
    -> RUNNING

PermissionRequest
    -> WAITING

OpenAI notification removed while WAITING
    -> RUNNING
       (permission dialog/attention item resolved)

Stop
    -> DONE

OpenAI notification added shortly after Stop
    -> DONE_PENDING_ATTENTION

tracked post-Stop notification removed
    -> NORMAL
```

Repeated `PermissionRequest` events do not stack. A `Stop` event always terminates the active turn state.

## Loader warning

Codex accepted the installed hooks but reported:

```text
clamping SessionEnd hook timeout to 3s
```

The installer used a generic 5 second timeout. The configuration is corrected so `SessionEnd` uses 3 seconds, removing this loader warning while retaining 5 seconds for the other diagnostic hooks.

## Next gate

The dual-input source layer is now sufficiently proven for a dry-run state normalizer.

Next implementation should:

1. compute `NORMAL / RUNNING / WAITING / DONE_PENDING_ATTENTION / ERROR`;
2. expose the current normalized state in the Status Lab tray and journal;
3. keep K15 RGB writes disabled for one normalization canary;
4. after the normalized timeline is physically accepted, connect the state output to the already-proven restore-first K15 WebHID lighting path.
