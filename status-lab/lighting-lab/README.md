# VOROTEX K15 Lighting Lab

`Vorotex.K15.LightingLab.exe` is a local owner-only RGB reverse-engineering companion for Status Lab.

Safety rules:

- it never calls hardware profile selection and never switches Profile A/B programmatically;
- it observes the physically active slot only;
- before the first write on a slot it captures an exact lighting header plus all native mode records 0x81..0x88 used by experiments;
- every test starts from that exact baseline;
- `Restore exact baseline` restores the active profile only;
- application exit best-effort restores only the physically active profile;
- no macro, key mapping, power or firmware commands are used.

The UI exposes native modes seen in the OEM W910 WebDriver:

- Constant: one explicit color;
- Flowing water: palette + selection mask;
- Horse race / native 0x83: OEM UI has no color control; historical Status Lab name was `mono_water`;
- Single-color breathing: one explicit color;
- Cycle breathing: palette + selection mask;
- Tetris blocks: palette + selection mask;
- Neon: OEM UI has no color control;
- Ambilight: OEM UI has no color control;
- Off.

Every `Apply test` writes a JSONL record to:

`%LOCALAPPDATA%\VOROTEX\K15 Lighting Lab\lighting-lab.jsonl`

The record includes test id, active profile, native mode code, brightness, speed, direction, palette mask, colors/seed bytes, wire color order, baseline header, exact header/mode-record bytes written, readback result and the owner's note.

Recommended workflow:

1. close the OEM W910/VOROTEX WebDriver;
2. launch Lighting Lab from Status Lab tray; Status Lab disables its RGB notifier first;
3. physically choose Profile A or B;
4. select a mode and parameters;
5. click `Apply test`;
6. describe the physical result in the note field and save the note;
7. use auto-restore or `Restore exact baseline`;
8. physically switch profile when you want to test the other slot.
