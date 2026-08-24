# Status Lab owner canary 5 — 2026-08-24

Sanitized findings from the profile-switch / RGB-state owner canary. Raw local journals, notification text, machine-specific paths, session IDs and turn IDs are intentionally not committed.

## Result

```text
PROFILE_SWITCH_DETECTION = PASS
PROFILE_SWITCH_5S_OVERLAY = PASS_WITH_RESIDUE_BUG
PROFILE_A_BASELINE = RED_CONSTANT_CONFIRMED
PROFILE_B_BASELINE = BLUE_CONSTANT_EXPECTED_BUT_PREVIOUS_OVERLAY_PERSISTED
RGB_DISABLE = FAIL_WITH_INVALID_SLOT_EXCEPTION
RUNNING_WHITE_SLOW = REJECTED_BY_OWNER
WAITING_WHITE_FAST = WORKING_BUT_TOO_SIMILAR_TO_RUNNING
NORMAL_RESTORE = PASS_WITH_VISIBLE_TRANSIENT
R_G_WIRE_SWAP = RETAIN_G_R_B
```

## Persisted Profile B overlay

The journal proves the residue mechanism rather than a native Profile B setting change.

One run began on Profile A with the expected Constant mode (`0x81`). The owner switched to B and Status Lab started the five-second Profile B overlay. Before that B overlay completed, the owner switched back to A. No `rgb_profile_flash_completed` event for B was emitted before the A switch.

A later RGB enable while Profile B was active observed `originalMode = 0x84` (Single-color breathing), not the accepted blue Constant baseline. Therefore the previous canary had persisted its B breathing header into the onboard profile. This is why Profile B could continue blinking even after RGB automation was disabled.

Correction:

1. when the user switches A/B, remember the profile being left;
2. temporarily select that previous onboard slot with the proven `0x02 / selector 2` command;
3. restore its exact cached baseline;
4. immediately select the user's new slot again;
5. only then run the new profile's five-second overlay;
6. a first-seen A/B profile whose header is stale notifier mode `0x84` or `0x86` is self-healed back to Constant `0x81`, preserving the untouched Constant-mode data.

## Approval inside Codex

Approving permission directly in Codex does not have to remove the Windows toast. `PostToolUse` is therefore installed as a fifth lifecycle hook:

```text
PermissionRequest -> WAITING
successful PostToolUse -> RUNNING
```

Windows-notification removal remains an additional resume path, not the only path.

## Disable crash

The owner reproduced an unhandled .NET exception while disabling RGB:

```text
System.IO.InvalidDataException:
K15 returned an invalid active onboard slot.
```

The failure occurred through `ReadActiveSlot -> Restore -> DisableAsync` during a profile transition.

Correction:

- an invalid transient slot value is no longer a fatal `InvalidDataException`;
- active-slot reads retry until the device stabilizes, then surface a transport timeout if necessary;
- `DisableAsync` catches restore failures and always disposes the HID handle instead of allowing an async WinForms event handler to crash the process.

## RUNNING vs WAITING UX

The owner rejected white slow breathing vs white fast breathing because the two states are too similar by peripheral vision.

New policy:

```text
RUNNING  -> built-in Tetris blocks effect (0x86)
WAITING  -> white fast single-color breathing
DONE     -> green breathing
ERROR    -> red fast breathing, reserved for high-confidence error
NORMAL   -> exact red/blue profile baseline
```

Tetris is selected by changing the lighting mode header only. The existing onboard Tetris detail record is left untouched.

## Red/green transient and NORMAL red blink

The protocol still intentionally stores colors as G,R,B on the wire. The open W910 implementation uses the same byte ordering, and earlier physical tests identified the R/G inversion. The canary did reveal an ordering artifact: Status Lab previously switched the lighting header to breathing before writing the new breathing palette, briefly exposing old palette bytes. That could look like a wrong red/green flash.

Correction:

- notification/profile breathing: write hidden breathing detail first, activate the breathing header second;
- restore to NORMAL: write the Constant baseline header first, restore the hidden breathing record second.

This should remove the red blink seen immediately before NORMAL as well as stale-color flashes during state changes without reversing the proven G,R,B mapping.

## RGB transport failures are not semantic ERROR

A transient `0x82` HID failure is reported as transport `RETRYING` / `RECONNECTED`. It does not create semantic `ERROR` and does not request red error lighting. Toast keyword heuristics also remain forbidden from creating semantic ERROR because they previously produced false positives after `Stop`.

## Next owner gate

Run two smaller tests instead of one long mixed canary.

### A. Profile switching / cleanup only

```text
A NORMAL red constant
A -> B
B blue profile overlay for 5s
B -> blue constant

rapid B -> A before the 5s B overlay finishes
A red profile overlay
then red constant

RGB OFF
switch A/B manually
both profiles must remain constant baseline
no .NET exception
```

### B. Codex states

```text
UserPromptSubmit -> Tetris
PermissionRequest -> white fast breathing
approved/PostToolUse -> Tetris
Stop -> green breathing
completion removed or 15s -> current profile constant baseline
```

Profile switching during a state must still show the new profile color for five seconds and then resume the current state.
