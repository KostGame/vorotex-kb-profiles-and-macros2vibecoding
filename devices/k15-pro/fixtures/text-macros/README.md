# Generated standalone macro import canary

This directory contains sanitized public evidence for `TMAC-001A`. The
fixture describes one repository-serializer-generated standalone `.Macro.Config`
package that the owner imported through the official VOROTEX UI.

## Proven boundary

The current evidence status is:

```text
OFFICIAL_MACRO_EXPORT=PROVEN
OFFICIAL_MACRO_IMPORT_UI=PROVEN
UNMODIFIED_EXPORT_ROUNDTRIP_IMPORT=PASS
GENERATED_MACRO_CONFIG_IMPORT=PASS
STANDALONE_GENERATED_MACRO_TRANSPORT=PROVEN
MINIMALLY_MODIFIED_EXPORT_IMPORT=NOT_REQUIRED
```

`GENERATED_MACRO_CONFIG_IMPORT=PASS` is the relevant result for this task:
the generated package was transported as a standalone `.Macro.Config`,
imported by VOROTEX, and observed as group `TMAC_CANARY_GENERATED` containing
macro `TMAC_GEN_TEXT`. The imported macro had 14 active events, 1 ms active
delays, and GUI `Cycle = 1`, corresponding to `macRpt=1` and `rptType=0`.

The minimally modified native-export canary is not required for this round.
The generated package is a stronger proof of the serializer-owned standalone
path than a lightly edited native export. That diagnostic remains available
only if a future generated import fails and the failure needs forensic
isolation.

The public JSON records the sanitized semantic evidence:

- `visibleText=K15TEST`;
- `activeHidValues=14,14,30,30,34,34,23,23,8,8,22,22,23,23`;
- ordinary key down/up states `1,2` repeated seven times;
- `activeDelays=1 ms`;
- `arrayCapacity=500`;
- `macRpt=1`, `rptType=0`.

The deterministic GUIDs in the example are placeholders. Real native GUIDs,
machine paths, raw exports, and the private generated package are deliberately
excluded. The `.KB.Config` profile-installation path is a separate contract and
is not replaced by this standalone macro fixture.

## What this does not prove

This canary proves import and native-shape transport for the generated
`K15TEST` package. It does not establish general uppercase/lowercase behavior
with Caps Lock off, Shift ordering, punctuation, Space behavior, or arbitrary
Russian character coverage. The repository's existing RU/EN selector facts
remain useful, but the fresh authoritative recorder-to-export EN and RU probes
were not captured by TMAC-001A. Those exact gaps are recorded in
`manifest.example.json`; consequently the parent acceptance matrix remains
`TMAC001_PARENT_READY_TO_CLOSE=NO`.

No device write, firmware operation, direct `macroConfig` mutation, or live
keyboard action is part of this public fixture. It is evidence for the
serializer/import boundary only.

## Files

- `generated-standalone-canary.example.json` — sanitized event and provenance
  record;
- `manifest.example.json` — status, owner observation, acceptance matrix, and
  publication boundary.
