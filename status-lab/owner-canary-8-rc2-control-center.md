# Owner Canary 8 · RC2 Control Center

Baseline: RC1 `404e2be9474a51ded10c98d13de50ef3ae480e29`.

## Functional canary

1. Launch Status Lab RC2.
2. Double-click tray icon. Control Center must open.
3. Confirm state, transition reason, elapsed time, Codex session/cwd and RGB status update while Codex works.
4. Trigger PermissionRequest and approve inside Codex. Expected `WAITING -> RUNNING` via PreToolUse/PostToolUse as in RC1.
5. Let one task finish. Expected STOP overlay, DONE, then existing notification/manual/30s NORMAL behavior. Merely foregrounding Codex is intentionally unchanged in this RC2.
6. Toggle RGB from Control Center. Existing physical colors/effects must remain RC1-equivalent.
7. Use `Restore lighting` only on the physically active profile and verify exact native baseline.
8. Toggle Windows autostart ON, confirm Control Center reports ON; toggle OFF and confirm OFF.
9. Verify hooks health reports current `.codex` / `.codex-agentloop` state and `Install/update hooks` repairs a stale/missing handler set.
10. Confirm Lighting Lab and Advanced configurator still open.

## Sleep research canary

This is a read/capture experiment, not a device-write feature.

1. In Control Center press `1 · Capture BEFORE`.
2. Open official VOROTEX from the provided button or manually.
3. Change exactly ONE sleep/standby timeout value. Do not change profile, lighting or mappings in the same experiment.
4. Save/apply using official VOROTEX.
5. Return to Control Center and press `2 · Capture AFTER + diff`.
6. Inspect the generated local report under:

   `%LOCALAPPDATA%\VOROTEX\K15 Status Lab\device-settings-research\<timestamp>\report.txt`

7. Send `report.txt` and, if needed, `report.json` back for protocol classification. Do not publish raw before/after vendor copies.

Repeat with at least two different sleep values, preferably `5 -> 10` and `10 -> 30`, to distinguish a real timeout field from unrelated volatile changes.

## Safety gate

Expected throughout:

```text
PROGRAMMATIC_PROFILE_SELECTION = NO
UNKNOWN_HID_POWER_WRITES = NONE
FIRMWARE_ACTIONS = NONE
KEY_OR_MACRO_WRITES = NONE
DONE_FOREGROUND_CODEX_HEURISTIC = NONE
```

## Acceptance

```text
CONTROL_CENTER_UI = PASS | FAIL
STATE_REASON_ELAPSED = PASS | FAIL
HOOK_HEALTH = PASS | FAIL
AUTOSTART = PASS | FAIL
RC1_RGB_REGRESSION = PASS | FAIL
RECOVERY = PASS | FAIL
LIGHTING_LAB_RETAINED = PASS | FAIL
CONFIGURATOR_RETAINED = PASS | FAIL
SLEEP_CAPTURE = PASS | FAIL
SLEEP_FIELD_CANDIDATE = <path/line | UNKNOWN>
```
