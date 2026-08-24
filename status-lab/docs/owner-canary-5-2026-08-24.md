# Status Lab owner canary 5 — 2026-08-24

Sanitized findings from the next physical owner run. Raw journal, notification text, session IDs and machine-specific details are intentionally not committed.

## Observed problems

### 1. Approval inside Codex can leave normalized WAITING

The owner approved the system permission from the Codex UI itself. The Windows toast did not provide a reliable acknowledgement/removal signal, so the previous reducer could remain in `WAITING` until a later `Stop` or manual action.

Correction:

```text
PermissionRequest -> WAITING
successful PostToolUse -> RUNNING
```

`PostToolUse` is now installed as a fifth Codex hook. Windows-notification removal remains an additional resume path, not the only path.

### 2. RGB transport error appeared after DONE

The tray showed:

```text
state = DONE_PENDING_ATTENTION
RGB = ERROR: No matching K15 HID response for command 0x82
```

This was not a semantic Codex error. The normalized state remained `DONE_PENDING_ATTENTION`; only the K15 active-slot HID read timed out.

Correction:

- HID read requests retry the complete request with fresh sequence IDs;
- transient transport failures are reported as `RGB: RETRYING` / `RECONNECTED`;
- USB/HID transport failures never create semantic `ERROR` and never request red error lighting by themselves.

### 3. Profile switching is a normal UX action, not a safety failure

Owner baseline:

```text
Profile A = red
Profile B = blue
```

Required behavior:

```text
switch -> A
  red fast breathing for 5s
  then resume current notification state

switch -> B
  blue fast breathing for 5s
  then resume current notification state
```

If normalized state is `NORMAL`, the exact baseline of the newly selected profile is restored after the 5-second profile indication.

Status Lab now monitors the active onboard slot, keeps separate baseline snapshots per profile, and treats a profile switch as a temporary high-priority visual overlay.

### 4. Color policy simplified

Yellow/amber were not reliably distinguishable on the physical K15. State colors are restricted to primary, highly visible colors plus white:

```text
NORMAL A                exact red profile baseline
NORMAL B                exact blue profile baseline
RUNNING                 white slow breathing
WAITING                 white fast breathing
DONE_PENDING_ATTENTION  green breathing
ERROR                    red fast breathing (reserved for high-confidence failure source)
```

Toast text heuristics no longer create semantic `ERROR` because false positives were physically observed after `Stop`.

## Next owner gate

Verify the new build with:

1. approval inside Codex -> `WAITING` returns to `RUNNING` through `PostToolUse` even if the toast remains;
2. `DONE` does not turn the tray into semantic error on a transient `0x82` failure;
3. Profile A/B switch produces red/blue 5-second profile indication;
4. after 5 seconds the active notification state resumes;
5. `DONE` removal or 15-second timeout restores the exact baseline of the profile that is active at that moment.
