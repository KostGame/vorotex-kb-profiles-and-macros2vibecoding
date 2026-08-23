# Native RU Alpha import package (historical single-profile baseline)

The owner-tested single-profile baseline documented here used 10 ms event
timing. The current two-profile V1 RC supersedes it for new generation: see
[`two-profile-v1-rc1.md`](two-profile-v1-rc1.md), whose production default is
5 ms and whose Profile B Space action is safe continuation rather than
confirmation.

The generator separates the semantic RU profile from the VOROTEX serializer:

```text
devices/k15-pro/profiles/native-ru-alpha/profile.json
        -> tools/generate_native_ru_alpha.py
        -> K15_VIBECODING_RU_ALPHA.Macro.Config
        -> K15_VIBECODING_RU_ALPHA.KB.Config
```

The default input profile is forced `RU`, which means every localized RU text
macro first emits `Ctrl+Shift+2`, then its Russian HID text. `--layout EN`
emits `Ctrl+Shift+1` before the English semantic phrase and US HID positions.
The selector mapping is profile data, not a universal Windows claim. No
Unicode code points are written to the VOROTEX event arrays.

The corrected native export proves `VIBE_12_NEW_LINE_RU` as four events:

```text
Left Shift down, Enter down, Enter up, Left Shift up
macVal: 225, 40, 40, 225
macSta: 1, 1, 2, 2
macDly: 10, 10, 10, 10
```

This historical package used `macRpt=1`, `rptType=0`, and a 10 ms event delay.
There is no automatic submit event. The V1 RC generator makes the delay
configurable and defaults to 5 ms.

## Generate

From the task worktree, use the supplied native Profile B export as a local
template when a full official KB shape is required:

```text
python tools/generate_native_ru_alpha.py `
  --output-dir <runtime-output> `
  --layout RU `
  --kb-template "<owner-supplied Profile B export>"
```

The template is read-only and is not copied into the repository. The
serializer retains the native KB sections and replaces `MacroGrpInfo` with
the single intended RU Alpha group. Numeric, decimal-point, Enter, minus and
plus and Space bindings are emitted from observed native evidence. The
observed slot values 12/13/14 are profile-memory allocation evidence, not
universal semantic control IDs.

The joystick click is preserved as the native `btn_KBKey_Enter` key with
`KBKey=40`. It is a plain Enter submit action and does not consume an Alpha
macro or a new Alpha `MemMacId`.

## Manual owner test boundary

The generated files are test artifacts. Do not perform Import through this
agent. The owner should first back up the current VOROTEX configuration, then
use the official VOROTEX Import workflow and physically test the K15. No
firmware, reset, restore, AutoHotkey, joystick, encoder, or live configuration
mutation is part of this task.
