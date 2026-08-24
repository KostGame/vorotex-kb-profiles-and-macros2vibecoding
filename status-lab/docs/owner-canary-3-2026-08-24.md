# Status Lab owner canary 3 — 2026-08-24

This note records sanitized findings from the third Windows owner canary. Raw local journals, notification text, machine-specific paths, session IDs and turn IDs are intentionally not committed.

## Result

```text
DRY_RUN_NORMALIZER = PASS
NORMAL_TO_RUNNING = PASS
RUNNING_TO_WAITING = PASS
WAITING_TO_RUNNING_ON_TRACKED_NOTIFICATION_REMOVAL = PASS
WAITING_TO_DONE_ON_STOP = PASS
DONE_TO_RUNNING_ON_NEXT_PROMPT = PASS
POST_STOP_NOTIFICATION_TRACKING = PASS
FINAL_NORMAL_ACK = NOT_EXERCISED_IN_CAPTURE
```

## Observed normalized transitions

The new build emitted seven `normalized_state_changed` records:

```text
NORMAL
  -> RUNNING
  -> WAITING
  -> DONE_PENDING_ATTENTION

DONE_PENDING_ATTENTION
  -> RUNNING
  -> WAITING
  -> RUNNING
  -> DONE_PENDING_ATTENTION
```

The first tested turn moved directly from `WAITING` to `DONE_PENDING_ATTENTION` when `Stop` arrived. No correlated permission notification removal was observed before that `Stop`, which is a valid completion path.

The second tested turn exercised the full permission-resolution path:

```text
UserPromptSubmit
  -> RUNNING

PermissionRequest
  -> WAITING

tracked OpenAI notification added
  -> WAITING

same notification removed
  -> RUNNING

Stop
  -> DONE_PENDING_ATTENTION

post-Stop OpenAI notification added
  -> completion attention tracked
```

The capture ended while the final post-Stop notification was still present, so `DONE_PENDING_ATTENTION -> NORMAL` was not observed in this file. The reducer's unit smoke test covers that transition, and a later physical canary can close the tracked notification to exercise it.

## Timing

The normalizer's observed transition delay was roughly the expected reorder/poll budget:

- hook source timestamp -> normalized transition: approximately 0.4–0.6 s;
- notification removal -> normalized transition: approximately 0.5 s.

This is acceptable for a human-attention RGB indicator.

## Decision

The dry-run normalizer gate is accepted for an **opt-in RGB canary**.

The physical RGB stage must remain guarded:

1. RGB output defaults OFF;
2. user explicitly enables the canary from the tray;
3. current K15 lighting bytes are captured before the first write;
4. only the single-color-breathing mode record and lighting header may be changed;
5. exact original bytes must be restored on `NORMAL`, manual disable, and application exit;
6. the canary must refuse writes if the active onboard K15 profile changes while the snapshot is held;
7. keys, macros, power settings and firmware are out of scope.
