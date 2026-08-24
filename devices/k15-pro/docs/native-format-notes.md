# Native VOROTEX format notes

These notes are derived from owner-supplied native exports. The original exports remain outside this public repository.

## Macro export

The native root has exactly these fields:

```text
ForbidView, GrpGuid, GrpName, MacroInfo
```

`MacroInfo` contains 15 entries in the RU Alpha export. Each entry contains `BindKeys`, `ForbidView`, `MacroGuid`, `MacroName`, and `macData`. The native `macData` object contains `YStep`, `YStepEn`, `extVal`, `macDly`, `macRpt`, `macSta`, `macVal`, `num`, `numCpi`, `numLed`, `numMedia`, `numWhl`, `numXY`, and `rptType`; event arrays have capacity 500.

The before/after Cycle fixture changes only the target macro's `rptType` from 1 to 0 while retaining `macRpt=1`. The corrected NEW_LINE fixture proves:

```text
num=4
macVal=[225,40,40,225]
macSta=[1,1,2,2]
macDly=[10,10,10,10]
macRpt=1
rptType=0
```

`num` is the populated prefix length. The event arrays themselves are fixed-capacity native arrays; their unused tail is zero-filled. Do not validate native packages by requiring `num == len(macVal)`.

## Keyboard profile export

The native KB root has:

```text
KBconfig, MacroGrpInfo, SingleProfile
```

`KBconfig` contains `FnKey`, `FnKeyMacro`, `KBKey`, `KBKeyMacro`, `KBled`, and `KBmain`. A single-profile export has been observed with `SingleProfile=1`; an Export All fixture has `SingleProfile=0`. This is evidence for the tested VOROTEX version only.

Observed bindings prove `MemMacId` and GUID relationships for all 15 standard K15 profile controls. In the complete observed V1 allocation, Space uses slot 12, minus 13, plus 14, Enter 11, decimal 10, and the numeric controls occupy the remaining proven slots. Treat these as observed profile-memory allocation, not universal semantic IDs.

Joystick click is separately represented by `btn_KBKey_Enter` with native key value `40`; it remains plain Enter/Send and does not consume a text-macro slot.

## Encoder

The owner-proven Profile A encoder export changes only the ordinary KB fields:

```text
btn_KB_Scr_Up0 = 304
btn_KB_Scr_Dn0 = 305
```

The corresponding `FnKey` fields remain unchanged. These are the proven Profile A vertical-scroll values. Profile B retains its accepted prior encoder behavior.

## Lighting

Profile A and Profile B each use a native 14-record lighting bank. The generated V1 packages copy the intended bank exactly rather than reconstructing unknown LED fields.

A VOROTEX UI selection displayed as `#00FF00` produced a physically red keyboard in owner testing. Therefore the serialized value `0xFF00FF00` is preserved as an opaque native color value; RGB channel ordering remains unresolved and the generator must not swap channels speculatively.

## Import behavior

Official `.KB.Config` Import is the preferred user-facing installation path. Physical testing established that Import is non-pruning: repeated imports can leave stale/duplicate macro groups. Import results have also varied with application/profile state, so deterministic behavior from file bytes alone is not claimed.

Both a compact/minimal single-profile shape and a full-template shape have imported successfully in controlled tests. The accepted V1 release packages use the full-template serialization that was physically validated with 5 ms timing and trailing-space text behavior.

## Public package hygiene

Do not copy an owner export wholesale. Native re-exports can contain accumulated stale groups. Each generated single-profile V1 package must contain only the macro group required by that profile:

- Profile A: `K15_TOOLS_AUTH`;
- Profile B: `K15_VIBECODING_RU_ALPHA`.

Raw personal exports, machine paths and unrelated accumulated macro groups stay outside the public repository.
