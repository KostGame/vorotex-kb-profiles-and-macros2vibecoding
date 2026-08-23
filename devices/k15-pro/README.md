# VOROTEX K15 Pro

Public, sanitized research for using the VOROTEX K15 Pro as a compact vibecoding controller.

## Current proof status

Confirmed in controlled testing:

- all 15 standard physical keys have known VOROTEX storage slots;
- generated macro/profile JSON is parsed by the official VOROTEX application;
- generated multi-event macros execute correctly after a native GUI assignment;
- the resulting assignment survives closing VOROTEX and a keyboard power cycle;
- restoring the baseline assignment through the native GUI restores onboard behavior.

Practical apply path currently proven:

```text
generated config
    -> official VOROTEX GUI
    -> native GUI assignment/reassignment
    -> K15 onboard memory
```

Not yet proven:

- automatic device apply from generated files alone;
- the exact low-level device-sync trigger used by VOROTEX;
- physical storage schema for the rotary encoder and joystick;
- maximum number of onboard profiles beyond the two currently observed device slots.

## Manual install, K15 Pro

The root [`README.md`](../../README.md#manual-installation-of-generated-configuration-files) contains the full manual-install procedure. K15-specific locations, relative to the VOROTEX installation directory, are currently:

```text
res/KeyboardDock/KeyboardA/Config/Profile0.json   # UI Profile1
res/KeyboardDock/KeyboardA/Config/Profile1.json   # UI Profile2
res/KeyboardDock/KeyboardA/Config/DeviceFeature.ini
res/MacroDock/MacroData/macroConfig.json
```

Always back up the live files first. With VOROTEX closed, copy only the files included by a profile package. After starting VOROTEX, verify the imported macro/binding and use a **native GUI assignment/reassignment** for the affected physical key so the setting is written to K15 onboard memory. Replacing JSON files alone is not a proven device-apply mechanism.

Rollback likewise has two parts: restore the backed-up files, then reassign the original action through VOROTEX so the keyboard's onboard state matches the restored files.

## Contents

- [`docs/architecture.md`](docs/architecture.md) — working architecture and safety boundaries.
- [`docs/native-format-notes.md`](docs/native-format-notes.md) — sanitized native export schema evidence.
- [`docs/native-import-ru-alpha.md`](docs/native-import-ru-alpha.md) — RU Alpha serializer and owner import boundary.
- [`docs/physical-layout.md`](docs/physical-layout.md) — human-readable control map.
- [`docs/vibecoding-v1.md`](docs/vibecoding-v1.md) — first vibecoding UX draft.
- [`docs/two-profile-v1-rc1.md`](docs/two-profile-v1-rc1.md) — Profile A/Profile B V1 RC semantics and package boundary.
- [`schema/physical-layout.json`](schema/physical-layout.json) — machine-readable confirmed mappings.

## Safety

This public directory intentionally excludes live VOROTEX files, local backups, GUID-bearing research captures, firmware, raw device dumps, and machine-specific paths.
