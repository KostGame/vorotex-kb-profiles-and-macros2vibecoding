# Native RU Alpha import package (historical single-profile baseline)

This document describes the earlier owner-tested RU Alpha research baseline. It is retained for provenance and native-format evidence, not as the current V1 layout source of truth.

For current Profile A/Profile B layout work, use:

- [`layout-development-baseline.md`](layout-development-baseline.md)
- [`profile-a-tools-auth.md`](profile-a-tools-auth.md)
- [`profile-b-main-vibecoding.md`](profile-b-main-vibecoding.md)
- [`../schema/v1-layout-baseline.json`](../schema/v1-layout-baseline.json)

The historical single-profile baseline used 10 ms event timing. The accepted two-profile V1 packages use a physically imported 5 ms full-template baseline. Profile B Space is safe continuation rather than confirmation.

## Historical RU Alpha model

The historical profile description is stored at:

```text
devices/k15-pro/profiles/native-ru-alpha/profile.json
```

It preserves the earlier RU Alpha evidence state. The current generator has evolved beyond that file and now contains the accepted two-profile A/B semantic model directly; do not treat the historical JSON as the generator's current input source.

The proven selector configuration is:

```text
RU -> Ctrl+Shift+2
EN -> Ctrl+Shift+1
```

These are owner-machine selectors, not universal Windows defaults. Current V1 text macros use RU self-selection where Russian text is emitted, while EN selection is used where the macro must type US-layout characters such as the three-backtick code fence or the `push/merge` segment. The current public generator does not claim a complete standalone English semantic profile merely because the EN selector itself is proven.

No Unicode code points are written to VOROTEX event arrays; localized text is compiled to physical HID positions for the selected layout.

## Native NEW_LINE evidence

The corrected native export proves `VIBE_12_NEW_LINE_RU` as four events:

```text
Left Shift down, Enter down, Enter up, Left Shift up
macVal: 225, 40, 40, 225
macSta: 1, 1, 2, 2
macDly: 10, 10, 10, 10
```

The historical package used `macRpt=1`, `rptType=0`, and a 10 ms event delay. There is no automatic submit event. The accepted V1 generator defaults to 5 ms; 1 ms remains research-only until physically tested.

## Current generation boundary

`tools/generate_native_ru_alpha.py` generates the accepted two-profile package model. For the full native V1 shape it uses an owner-supplied native structural template plus the separately proven profile-lighting fixture. Those raw fixtures remain outside the public repository.

The serializer preserves native KB sections and emits only the intended profile macro group for each generated package. Numeric keys, decimal point, Enter, minus, plus and Space use observed binding evidence. `MemMacId` values are profile-memory allocation evidence for the observed complete profile state, not universal semantic IDs.

Joystick click is preserved as `btn_KBKey_Enter` with `KBKey=40`, a native Enter/Send action that consumes no text-macro slot.

## Current V1 owner-test boundary

Profile A and Profile B have both been physically imported and validated. The accepted behaviors include:

- 5 ms text timing;
- one trailing ASCII space on ordinary text commands;
- physical Enter = Shift+Enter;
- joystick click = native Enter/Send;
- A2 Paste = Ctrl+V followed by Shift+Enter;
- A0 report-from-clipboard = opening fence only, paste inside the code block, safe newline after paste, no closing fence, no auto-submit;
- Profile A encoder rotation = vertical scroll through `btn_KB_Scr_Up0=304` and `btn_KB_Scr_Dn0=305`;
- profile lighting banks preserved from native exports.

VOROTEX Import is non-pruning and can leave duplicate groups. Import behavior has also shown state dependence, so file bytes alone are not treated as the complete importer state model.
