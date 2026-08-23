# Native VOROTEX format notes

These notes are derived from the six owner-supplied exports. The original
exports remain outside this public repository.

## Macro export

The native root has exactly these fields:

```text
ForbidView, GrpGuid, GrpName, MacroInfo
```

`MacroInfo` contains 15 entries in the RU Alpha export. Each entry contains
`BindKeys`, `ForbidView`, `MacroGuid`, `MacroName`, and `macData`. The native
`macData` object contains `YStep`, `YStepEn`, `extVal`, `macDly`, `macRpt`,
`macSta`, `macVal`, `num`, `numCpi`, `numLed`, `numMedia`, `numWhl`, `numXY`,
and `rptType`; event arrays have capacity 500.

The before/after Cycle fixture changes only the target macro's `rptType` from
1 to 0 while retaining `macRpt=1`. The corrected NEW_LINE fixture proves:

```text
num=4
macVal=[225,40,40,225]
macSta=[1,1,2,2]
macDly=[10,10,10,10]
macRpt=1
rptType=0
```

## Keyboard profile export

The native KB root has:

```text
KBconfig, MacroGrpInfo, SingleProfile
```

`KBconfig` contains `FnKey`, `FnKeyMacro`, `KBKey`, `KBKeyMacro`, `KBled`, and
`KBmain`. The current Profile B export has `SingleProfile=1`; the paired
`Export all` fixture has `SingleProfile=0`. This is an evidence-backed mode
observation only; it does not generalize to other VOROTEX versions.

The supplied Profile B pair proves `MemMacId` and GUID relationships for the
numeric keypad slots, decimal point, and Enter. A later observed Profile B
state proves serialized macro bindings for minus (`MemMacId=13`) and plus
(`MemMacId=14`). A complete native profile then proves Space at
`MemMacId=12`. These are scoped to observed profile-memory allocation states,
not universal semantic control IDs.

The joystick click is separately represented by `btn_KBKey_Enter` with native
key value `40`; it remains a plain Enter and is not converted into another
Alpha text macro.

The package contains only the intended `K15_VIBECODING_RU_ALPHA` group. This
avoids copying unrelated groups from the user's local profile into a public
artifact.
