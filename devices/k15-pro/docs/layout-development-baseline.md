# K15 Pro layout-development baseline

Status: **canonical working baseline for future layout work**.

This document freezes the hardware, UX and VOROTEX facts that were physically validated during the V1 profile work. Future layout experiments should start here and explicitly record any intentional divergence.

## Product model

Language is a property of a text macro, not a hardware profile.

```text
hardware profile
  -> semantic action
  -> optional target keyboard layout
  -> HID event sequence
```

The current design uses two onboard profiles:

- **Profile A — TOOLS / AUTH**: clipboard, editing utilities, report helpers and deliberate confirmation.
- **Profile B — MAIN / VIBECODING**: frequent architect / coding commands.

The profiles intentionally share the same positions for several high-frequency global actions so muscle memory survives profile switching.

## Physically proven physical-key storage

| Physical control | VOROTEX storage field |
|---|---|
| `1` | `btn_KBKey_KeyPad1` |
| `2` | `btn_KBKey_KeyPad2` |
| `3` | `btn_KBKey_KeyPad3` |
| `4` | `btn_KBKey_KeyPad4` |
| `5` | `btn_KBKey_KeyPad5` |
| `6` | `btn_KBKey_KeyPad6` |
| `7` | `btn_KBKey_KeyPad7` |
| `8` | `btn_KBKey_KeyPad8` |
| `9` | `btn_KBKey_KeyPad9` |
| `0` | `btn_KBKey_KeyPad0` |
| `.` | `btn_KBKey_KeyPadPoint` |
| physical Enter | `btn_KBKey_KeyPadEnter` |
| `-` | `btn_KBKey_KeyPadSub` |
| `+` | `btn_KBKey_KeyPadAdd` |
| Space | `btn_KBKey_Space` |
| joystick click | `btn_KBKey_Enter` |

Joystick click is intentionally native Enter with `KBKey=40`; it does not consume a macro-memory slot in the accepted V1 design.

## Proven macro-memory allocation for the 15 macro keys

The observed complete profile allocation is:

```text
0      -> MemMacId 0
2      -> MemMacId 1
1      -> MemMacId 2
3      -> MemMacId 3
4      -> MemMacId 4
5      -> MemMacId 5
6      -> MemMacId 6
7      -> MemMacId 7
8      -> MemMacId 8
9      -> MemMacId 9
.      -> MemMacId 10
Enter  -> MemMacId 11
Space  -> MemMacId 12
-      -> MemMacId 13
+      -> MemMacId 14
```

`MemMacId` is a profile macro-memory slot allocation, not a permanent semantic identifier for a physical control.

## Global V1 UX invariants

- ordinary text commands append exactly one ASCII Space;
- no automatic punctuation is appended;
- release text-event delay is `5 ms` in the physically accepted full-template packages;
- physical Enter is `Shift+Enter` / safe new line;
- joystick click is native Enter / explicit Send;
- no ordinary command auto-submits;
- clipboard Paste performed by a K15 semantic macro is followed by `Shift+Enter`;
- Profile A confirmation is separated from Profile B safe continuation;
- joystick directions remain unchanged/reserved for later layout work;
- RGB channel ordering is unresolved and must not be normalized by assumption.

## Language forcing

The proven owner configuration uses direct Windows input-profile selectors:

```text
EN -> Ctrl+Shift+1
RU -> Ctrl+Shift+2
```

Text macros select the target layout first, then emit physical HID usages for that layout. These selectors are not universal Windows defaults and must remain configurable.

Canonical selector event order:

```text
Ctrl down
Shift down
digit down
digit up
Shift up
Ctrl up
```

Physically proven:

- EN can be selected from RU and English text emitted correctly;
- RU can be selected from EN and Russian text emitted correctly;
- Cyrillic `г` maps to physical `U`, HID usage `24`.

## Profile-level peripheral behavior

### Profile A

- encoder rotate up: vertical scroll up, `btn_KB_Scr_Up0 = 304`;
- encoder rotate down: vertical scroll down, `btn_KB_Scr_Dn0 = 305`;
- lighting bank is preserved from the accepted native profile export;
- encoder click/profile switching is preserved rather than redesigned.

### Profile B

- keep the physically accepted encoder/profile-switch behavior unless a future task explicitly changes it;
- lighting bank should be preserved from its accepted native profile state.

## Official installation model

Preferred user-facing installation is the official VOROTEX `.KB.Config` Import path.

A `.KB.Config` can carry:

- keyboard profile data;
- physical key bindings;
- `KBKeyMacro` references;
- embedded macro groups / definitions;
- profile lighting data.

A standalone `.Macro.Config` remains useful as a secondary library/debug artifact, but is not required for the one-file single-profile installation path.

## Import caveats

VOROTEX Import is **non-pruning**. Repeated imports may leave old groups and create collision-suffixed entities. A backup/restore or another Import must not be described as a guaranteed clean rollback.

Import behavior has also shown state dependence: the same file bytes were observed in both crash and successful-import contexts. File-format conclusions must therefore distinguish:

- byte/structure compatibility;
- current VOROTEX application state;
- pre-existing groups/names/GUIDs;
- import order and restart state.

Do not implement destructive automatic cleanup without a separate explicit decision.

## Native macro representation facts

- GUI `Cycle = 1` serializes as `macRpt=1`, `rptType=0`.
- macro event arrays are fixed-capacity native arrays; `num` identifies the populated prefix rather than the physical array length.
- tests should validate the active prefix and native-compatible zero-filled tail, not require `num == len(array)`.

## Evidence vocabulary

Use these terms consistently:

- **PHYSICALLY PROVEN**: observed on the real K15 / official VOROTEX workflow.
- **NATIVE EXPORT PROVEN**: directly evidenced by official VOROTEX export structure.
- **STRUCTURALLY VALIDATED**: generated/parsed/tested offline but not sufficient alone to claim device behavior.
- **UNRESOLVED**: do not infer or silently implement.

## What may change safely in future layout work

Preferred layout iterations change semantic assignments while preserving the proven transport/serialization layer. Before changing a physical control, identify separately:

1. physical storage field;
2. semantic action;
3. language policy;
4. HID/native implementation;
5. macro-memory slot;
6. lighting/encoder fields that must remain preserved;
7. physical acceptance test.

See [`layout-change-protocol.md`](layout-change-protocol.md) for the change workflow and [`research-backlog.md`](research-backlog.md) for intentionally unresolved topics.
