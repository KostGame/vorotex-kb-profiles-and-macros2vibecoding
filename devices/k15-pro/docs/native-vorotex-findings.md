# Native VOROTEX findings relevant to layout work

This document collects format/runtime facts that should not have to be rediscovered during every layout iteration.

## Official export/import formats

### `.Macro.Config`

Used for standalone macro groups. Native exports preserve macro names, GUIDs, event arrays, delays and repeat/cycle settings.

TMAC-001A adds a sanitized generated-package canary under
[`../fixtures/text-macros/`](../fixtures/text-macros/). The owner imported the
serializer-generated standalone package through the official VOROTEX UI and
observed the expected generated group and macro. This proves the generated
standalone transport for the recorded `K15TEST` shape; it does not turn the
canary into a general proof of uppercase, Shift, punctuation, Space, or RU
character behavior.

The status boundary is explicit:

```text
UNMODIFIED_EXPORT_ROUNDTRIP_IMPORT=PASS
GENERATED_MACRO_CONFIG_IMPORT=PASS
STANDALONE_GENERATED_MACRO_TRANSPORT=PROVEN
MINIMALLY_MODIFIED_EXPORT_IMPORT=NOT_REQUIRED
```

The minimally modified native-export case remains an optional forensic
diagnostic only. The generated package is the stronger serializer-path proof;
run the minimal native comparison only if a future generated import fails.

### `.KB.Config`

A single-profile package can contain:

- `KBconfig`;
- ordinary key assignments;
- macro-binding references;
- `MemMacId` values;
- embedded `MacroGrpInfo` definitions;
- `SingleProfile=1`;
- profile lighting data.

This makes `.KB.Config` the preferred one-file installation unit for a layout/profile.

## Embedded macro references

A macro-bound physical key uses:

```text
KBKey.<field> = 700
KBKeyMacro.<field>.grpGuid -> embedded MacroGrpInfo[].GrpGuid
KBKeyMacro.<field>.macGuid -> embedded MacroInfo[].MacroGuid
KBKeyMacro.<field>.MemMacId -> profile macro-memory slot
```

All three parts must remain internally consistent.

## Cycle = 1

Direct native export comparison proved:

```text
GUI Cycle = 1
macRpt = 1
rptType = 0
```

The earlier assumption `rptType=1` is obsolete for this proven GUI state.

## Macro event arrays

VOROTEX uses fixed-capacity native arrays. `macData.num` is the active-prefix count, not necessarily the physical array length.

Validation must therefore check:

```text
active = [0:num]
```

and ensure the corresponding `macVal`, `macSta`, `macDly` active prefixes align. Native-compatible unused tails should remain in their expected zero-filled form.

Do not use the invalid invariant:

```text
num == len(macVal) == len(macSta) == len(macDly)
```

when the native arrays have fixed capacity.

## Text event states

Controlled macros established the ordinary keyboard event pair:

```text
state 1 -> key down
state 2 -> key up
```

## Language compiler

The K15 does not need Unicode text injection for the proven RU/EN workflow. The compiler emits physical HID usages for the target keyboard layout after selecting that layout.

Example concept:

```text
Russian text
-> SELECT_RU
-> map characters to physical keys in RU layout
-> emit HID down/up events
```

Known correction:

```text
Cyrillic г -> physical U -> HID usage 24
```

## Text timing

The accepted full-template V1 packages use `5 ms` active event delays and have physically imported/worked.

Important nuance: earlier tests produced both crashes and passes around related packages, and the same package bytes were observed under different import outcomes. Therefore do not reduce import compatibility to one numeric delay rule without a controlled state-aware experiment.

Keep timing configurable and distinguish:

- generated structure;
- application/importer state;
- device execution reliability.

## Minimal vs full profile shape

Both forms have appeared in successful native workflows:

- a minimal single-profile shape with 17 relevant `KBKey` / `KBKeyMacro` entries and empty Fn/LED sections was physically imported successfully at least once;
- a full-template shape with 240 key/Fn slots and 14 LED records was also physically imported successfully and is the accepted V1 generation baseline.

Therefore a 240-slot shape must not be documented as a universal minimum required by the importer.

For release generation, preserve the known-working full-template shape because it also carries native peripheral state predictably.

## Import is non-pruning

Physically observed behavior:

- repeated imports can leave old macro groups;
- name collisions can produce suffixed duplicate entities;
- backup/restore or importing another profile does not guarantee removal of stale groups.

Consequences:

- do not implement destructive cleanup implicitly;
- do not claim Import is a clean rollback;
- do not adopt a re-export wholesale if it contains accumulated unrelated groups;
- when using native exports as evidence, extract only the fields intentionally proven by that experiment.

## Import state dependence

Import success is not proven to be determined solely by file bytes. Future crash research should record before each attempt:

- application freshly restarted: yes/no;
- target profile already present: yes/no;
- same group GUID already present: yes/no;
- same group name already present: yes/no;
- package SHA-256;
- PASS/CRASH result.

Treat importer state as a first-class variable until isolated otherwise.

## Lighting (`KBled`)

`.KB.Config` contains profile-lighting state and importing a profile can change the physical lighting.

For accepted V1 packages, lighting banks should be copied from the intended native profile state rather than synthesized from guessed color semantics.

Observed anomaly:

```text
VOROTEX UI HEX: 00FF00
UI R/G/B: 0 / 255 / 0
physical keyboard result: red
```

The serialized native value associated with that state must therefore be treated as opaque until channel ordering is proven. Do not auto-swap or normalize channels based on standard RGB/ARGB assumptions.

## Encoder storage, Profile A

Native before/after evidence proved:

```text
btn_KB_Scr_Up0: 234 -> 304
btn_KB_Scr_Dn0: 233 -> 305
```

with accepted semantics:

```text
304 -> vertical scroll up
305 -> vertical scroll down
```

The previous values were extended volume actions in the native value table.

## Joystick click

Accepted V1 model:

```text
storage: btn_KBKey_Enter
KBKey: 40
semantic: native Enter / Send
```

It is intentionally not macro-bound and therefore does not consume one of the 15 macro-memory slots.

## Export hygiene

Native re-exports are evidence, not automatically release templates. Before using one:

1. identify which profile/group each part belongs to;
2. detect accumulated stale macro groups;
3. avoid copying unrelated groups/GUIDs;
4. preserve unknown native fields only when they belong to the intended profile state;
5. publish sanitized/generated artifacts, not raw personal forensic exports.
