# VOROTEX K15 Pro

Public, sanitized research for using the VOROTEX K15 Pro as a compact vibecoding controller.

## Hardware family

For research and discovery purposes, this repository treats the **VOROTEX K15 Pro as a VOROTEX-branded analogue of the W909 / SXS-W909 hardware family**.

That label describes the shared hardware concept and externally visible control layout. It does not mean that firmware, drivers, identifiers, local configuration files, or low-level write protocols are proven interchangeable.

See [`docs/w909-compatibility.md`](docs/w909-compatibility.md).

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
- maximum number of onboard profiles beyond the two currently observed device slots;
- binary/configuration compatibility with W909 / SXS-W909 variants.

## Multilingual VIBECODING v1

Language support is implemented above the hardware layer:

```text
physical K15 key
    -> stable semantic action
    -> selected locale pack
    -> host-side dispatcher
    -> localized text or app action
```

The same semantic layout is used for Russian, English, German, Italian, Simplified Chinese, Spanish, French, Brazilian Portuguese, Japanese, and Korean.

Canonical files:

- [`profiles/vibecoding-v1/semantic-map.json`](profiles/vibecoding-v1/semantic-map.json) — physical key to semantic action;
- [`profiles/vibecoding-v1/index.json`](profiles/vibecoding-v1/index.json) — language registry;
- [`profiles/vibecoding-v1/locales/`](profiles/vibecoding-v1/locales/) — localized command packs.

This is deliberately a dispatcher-oriented format, not a claim that every Unicode phrase can be stored and emitted reliably as a native K15 HID text macro. Chinese, Japanese, and Korean in particular can involve IME state, so direct Unicode insertion on the host is the preferred model.

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
- [`docs/physical-layout.md`](docs/physical-layout.md) — human-readable control map.
- [`docs/vibecoding-v1.md`](docs/vibecoding-v1.md) — VIBECODING v1 UX and multilingual model.
- [`docs/w909-compatibility.md`](docs/w909-compatibility.md) — W909-family analogue statement and evidence boundary.
- [`schema/physical-layout.json`](schema/physical-layout.json) — machine-readable confirmed mappings.
- [`schema/vibecoding-language-pack.schema.json`](schema/vibecoding-language-pack.schema.json) — locale-pack schema.
- [`schema/vibecoding-semantic-map.schema.json`](schema/vibecoding-semantic-map.schema.json) — semantic-map schema.
- [`profiles/vibecoding-v1/`](profiles/vibecoding-v1/) — canonical multilingual VIBECODING profile data.

## Safety

This public directory intentionally excludes live VOROTEX files, local backups, GUID-bearing research captures, firmware, raw device dumps, and machine-specific paths.
