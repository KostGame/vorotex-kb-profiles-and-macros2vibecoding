# VOROTEX keyboards to vibecoding

Community research, reproducible profiles, macro generators, and app-aware control layers for turning compact VOROTEX macro keyboards into vibecoding controllers.

The first supported device is the **VOROTEX K15 Pro**.

## Goals

- document physical-control to VOROTEX configuration mappings;
- keep generated profiles reproducible and reviewable;
- separate hardware triggers from app-specific actions where useful;
- preserve a safe path through the official VOROTEX software for device writes;
- publish only sanitized research artifacts suitable for a public repository.

## Devices

- [`devices/k15-pro/`](devices/k15-pro/) — VOROTEX K15 Pro research and VIBECODING profile design.

## Manual installation of generated configuration files

For K15 Pro, the currently proven practical path uses generated files plus the official VOROTEX application.

1. **Close VOROTEX completely** and make sure `VOROTEX-K15-PRO.exe` is no longer running.
2. **Back up your current VOROTEX configuration outside this repository.** At minimum keep copies of `Profile0.json`, `Profile1.json`, `macroConfig.json`, and `DeviceFeature.ini`.
3. Copy the released/generated profile file into the matching profile slot under the VOROTEX installation. For a Profile2 package this is normally `res/KeyboardDock/KeyboardA/Config/Profile1.json` relative to the VOROTEX install directory.
4. Copy the released/generated `macroConfig.json` to `res/MacroDock/MacroData/macroConfig.json` relative to the VOROTEX install directory.
5. Start VOROTEX with the K15 Pro connected and verify that the expected macros and key assignments appear in the GUI.
6. **Reassign the target key(s) through the native VOROTEX GUI.** Controlled testing showed that merely replacing the local JSON files is not sufficient to prove that the new assignment has been written to onboard keyboard memory. A native GUI assignment/reassignment is the currently proven device-write path.
7. Test the physical key output, then close VOROTEX and test again. For an onboard profile, also power-cycle/reconnect the keyboard and verify the assignment still works without reopening VOROTEX.

To roll back, restore your backed-up files with VOROTEX closed, reopen VOROTEX, and reassign the original key action through the native GUI so the onboard state is restored as well.

Do **not** use Firmware, Update, Reset, or Restore actions as part of these instructions. Never overwrite your only copy of a working configuration.

See [`devices/k15-pro/README.md`](devices/k15-pro/README.md) for device-specific proof status and details.

## Design reference

The vibecoding UX is informed by the OpenAI + Work Louder **Codex Micro** control philosophy: workflow actions on a joystick, frequently used commands on dedicated keys, reasoning control on a rotary encoder, and status feedback through lighting.

Reference: https://openai.com/supply/co-lab/work-louder/

This repository is an independent community project and is not affiliated with or endorsed by VOROTEX, OpenAI, or Work Louder.

## Public-repository safety

Do not commit live VOROTEX installation files, local baselines, device dumps, forensic captures, account data, secrets, or machine-specific paths. Sanitized examples must use explicit `.example.*` names rather than live vendor filenames.

See each device directory for current proof status and unresolved areas.
