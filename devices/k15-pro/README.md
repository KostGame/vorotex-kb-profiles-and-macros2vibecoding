# VOROTEX K15 Pro

Public, sanitized research for using the VOROTEX K15 Pro as a compact vibecoding controller.

## Current proof status

Confirmed in controlled testing:

- Profile A and Profile B `.KB.Config` packages import successfully;
- all 15 bindings and embedded macros work in both profiles;
- RU/EN layout forcing works from the configured direct selectors;
- Profile A/B `.KB.Config` packages include embedded macro groups and physical key → macro GUID bindings;
- Profile A serialized lighting is preserved by the embedded native LED bank;
- Profile B serialized lighting is also preserved, but physical V1.2 owner testing observed white lighting after Import; this is an accepted non-blocking discrepancy;
- Profile A encoder rotation uses vertical scroll (`304`/`305`);
- physical Enter emits Shift+Enter and joystick click remains native Enter/Send;
- A0 report-from-clipboard and A2 paste-plus-safe-newline work physically;
- V1.2 ordinary text at 1 ms and layout/structural events at 5 ms work physically;
- ordinary text commands append exactly one trailing ASCII space.

Practical apply path currently proven:

```text
generated .KB.Config
    -> official VOROTEX Import
    -> embedded macros + key bindings
    -> K15
```

For the supported Profile A/B packages, official profile Import is the canonical installation boundary. Direct replacement of live `Profile*.json` / `macroConfig.json` files is legacy research only.

Still intentionally bounded or unresolved:

- deterministic physical lighting parity after Import, especially Profile B;
- universal RGB channel ordering beyond the preserved native serialized banks;
- maximum number of onboard profiles beyond the two observed device slots;
- joystick directions and other controls outside the proven profile scope.

## Installation, K15 Pro

Use the official VOROTEX `.KB.Config` Import workflow for the current generated profiles. The imported package carries the supported embedded macro group and physical key bindings.

Current canonical release:

`devices/k15-pro/releases/v1.2-rc1/`

Import both Profile A and Profile B `.KB.Config` files, verify the resulting macros and bindings in VOROTEX, then test the real keyboard. VOROTEX Import is non-pruning and repeated imports can leave duplicate or stale macro groups.

The old live-file locations below remain relevant only for forensic/recovery research, not normal installation:

```text
res/KeyboardDock/KeyboardA/Config/Profile0.json
res/KeyboardDock/KeyboardA/Config/Profile1.json
res/KeyboardDock/KeyboardA/Config/DeviceFeature.ini
res/MacroDock/MacroData/macroConfig.json
```

Always keep backups before low-level experiments. Do not use firmware/update/reset/restore actions as part of profile installation.

## Contents

- [`docs/architecture.md`](docs/architecture.md) — working architecture and safety boundaries.
- [`docs/native-format-notes.md`](docs/native-format-notes.md) — sanitized native export schema evidence.
- [`docs/native-import-ru-alpha.md`](docs/native-import-ru-alpha.md) — RU Alpha serializer and owner import boundary.
- [`docs/physical-layout.md`](docs/physical-layout.md) — human-readable control map.
- [`docs/vibecoding-v1.md`](docs/vibecoding-v1.md) — first vibecoding UX draft.
- [`docs/two-profile-v1-rc1.md`](docs/two-profile-v1-rc1.md) — Profile A/Profile B V1 RC semantics and package boundary.
- V1.1 RC1 changes Profile B `+` to `Принимается ` while preserving its proven slot, MemMacId, and macro GUID.
- [`releases/v1.2-rc1/`](releases/v1.2-rc1/) — current physically accepted baseline: FULL/SHORT STATUS split, corrected SAFE_CONTINUE punctuation, 1 ms text timing, 5 ms layout/structural timing.
- [`schema/physical-layout.json`](schema/physical-layout.json) — machine-readable confirmed mappings.

## Safety

This public directory intentionally excludes live VOROTEX files, local backups, GUID-bearing research captures, firmware, raw device dumps, and machine-specific paths.
