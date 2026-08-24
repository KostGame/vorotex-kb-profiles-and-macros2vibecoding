# VOROTEX K15 Pro

Public, sanitized research for using the VOROTEX K15 Pro as a compact vibecoding controller.

## Current proof status

Confirmed in controlled testing:

- Profile A and Profile B `.KB.Config` packages import successfully;
- all 15 bindings and embedded macros work in both profiles;
- RU/EN layout forcing works from the configured direct selectors;
- Profile A lighting is preserved by the embedded native LED bank;
- Profile A encoder rotation uses vertical scroll (`304`/`305`);
- physical Enter emits Shift+Enter and joystick click remains native Enter/Send;
- A0 report-from-clipboard and A2 paste-plus-safe-newline work physically;
- 5 ms event timing and one trailing ASCII space on ordinary text commands work.

Practical apply path currently proven:

```text
generated config
    -> official VOROTEX GUI
    -> native GUI assignment/reassignment
    -> K15 onboard memory
```

Still intentionally bounded or unresolved:

- automatic device apply from generated files alone and the low-level sync trigger;
- universal RGB channel ordering (the native serialized value is preserved exactly);
- maximum number of onboard profiles beyond the two observed device slots;
- joystick directions and other controls outside the proven profile scope.

## Manual install, K15 Pro

The root [`README.md`](../../README.md#manual-installation-of-generated-configuration-files) contains the full manual-install procedure. K15-specific locations, relative to the VOROTEX installation directory, are currently:

```text
res/KeyboardDock/KeyboardA/Config/Profile0.json   # UI Profile1
res/KeyboardDock/KeyboardA/Config/Profile1.json   # UI Profile2
res/KeyboardDock/KeyboardA/Config/DeviceFeature.ini
res/MacroDock/MacroData/macroConfig.json
```

Always back up the live files first. Use the official VOROTEX `.KB.Config`
Import workflow, then verify the imported macros, bindings, lighting, and
encoder behavior in the native GUI. VOROTEX Import is non-pruning and repeated
imports can leave duplicate macro groups.

Rollback likewise has two parts: restore the backed-up files, then reassign the original action through VOROTEX so the keyboard's onboard state matches the restored files.

## Contents

- [`docs/architecture.md`](docs/architecture.md) — working architecture and safety boundaries.
- [`docs/native-format-notes.md`](docs/native-format-notes.md) — sanitized native export schema evidence.
- [`docs/native-import-ru-alpha.md`](docs/native-import-ru-alpha.md) — RU Alpha serializer and owner import boundary.
- [`docs/physical-layout.md`](docs/physical-layout.md) — human-readable control map.
- [`docs/vibecoding-v1.md`](docs/vibecoding-v1.md) — first vibecoding UX draft.
- [`docs/two-profile-v1-rc1.md`](docs/two-profile-v1-rc1.md) — Profile A/Profile B V1 RC semantics and package boundary.
- V1.1 RC1 changes Profile B `+` to `Принимается ` while preserving its proven slot, MemMacId, and macro GUID.
- [`schema/physical-layout.json`](schema/physical-layout.json) — machine-readable confirmed mappings.

## Safety

This public directory intentionally excludes live VOROTEX files, local backups, GUID-bearing research captures, firmware, raw device dumps, and machine-specific paths.
