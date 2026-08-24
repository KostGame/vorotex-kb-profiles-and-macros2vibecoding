# Status Lab owner canary 4 — 2026-08-24

This note records sanitized findings from the first physical RGB automation canary. Raw local journals, notification text, machine-specific paths, session IDs and turn IDs are intentionally not committed.

## Result

```text
RGB_ENABLE = PASS_WITH_ONE_TRANSIENT_RETRY
RGB_RUNNING = PASS
RGB_WAITING = PASS
RGB_DONE = PASS
RGB_MANUAL_RESTORE = PASS
RGB_AUTOMATIC_RESTORE = FAIL
WAITING_NOTIFICATION_CORRELATION = FAIL_FOR_EARLY_TOAST
BASELINE_COLOR_COLLISION = OBSERVED
```

## Physical observations

The K15 RGB canary opened the real device and captured a constant-lighting baseline (`originalMode = 0x81`). A later manual transition to `NORMAL` produced `rgb_restored`, confirming that exact-byte HID restoration itself works.

The failure was state-policy related rather than a failed HID restore:

```text
Stop
  -> DONE_PENDING_ATTENTION
  -> post-Stop notification remained present
  -> no automatic NORMAL transition
  -> owner manually acknowledged NORMAL
  -> exact original lighting restored
```

Therefore a persistent notification in Windows Notification Center must not keep the physical K15 in DONE indefinitely. DONE is changed to a bounded 15-second attention state, with earlier restoration if the tracked completion notification disappears.

## Early notification race

The canary captured this ordering:

```text
OpenAI notification added
~100 ms later: PermissionRequest hook
```

Because the first reducer only attached notifications that arrived *after* the hook, that notification was not tracked. Its later removal therefore did not produce `WAITING -> RUNNING`.

The reducer is corrected to bind the most recent unclaimed OpenAI notification that arrived up to two seconds before `PermissionRequest`. This handles the real cross-process race while keeping the correlation window tight.

## RUNNING color

The owner's normal K15 lighting already uses blue as one of its primary colors. Using blue for RUNNING made the state visually ambiguous.

Default RUNNING is changed to violet. WAITING remains amber, DONE green, ERROR red. Exact perceived hue may vary on the physical LED controller, so these are semantic defaults rather than calibrated colorimetry.

## Initial HID retry

One RGB enable attempt timed out on the active-slot read (`0x82`). A later retry succeeded, and subsequent read/write/readback operations completed normally. This remains a transient device-open/read condition to watch; no profile, macro, power, or firmware write was involved.

## Next physical gate

Re-run with the corrected build:

```text
baseline/original lighting
  -> UserPromptSubmit -> violet breathing
  -> PermissionRequest -> amber breathing
  -> permission toast resolved -> violet breathing
  -> Stop -> green breathing
  -> completion toast removed OR 15 seconds elapsed
  -> exact original lighting restored automatically
```

Manual `NORMAL` should no longer be required for the ordinary completion path.
