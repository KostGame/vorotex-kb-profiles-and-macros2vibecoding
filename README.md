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

## Local configurator

A first local-only configurator prototype lives in [`app/`](app/).

It is a static HTML/CSS/JavaScript application with no build step. Clone or pull the repository and open `app/index.html` directly in a modern browser.

Current v0.1 alpha support includes:

- opening official VOROTEX `.Macro.Config` and `.KB.Config` exports;
- inspecting macro groups, macros, profile bindings and low-level event arrays;
- preserving unknown fields while editing supported fields;
- renaming existing macros while keeping their GUIDs;
- applying the confirmed GUI `Cycle = 1` serialization (`macRpt=1`, `rptType=0`);
- validating the supported structural subset;
- exporting an edited file for native VOROTEX Import.

The intended user-facing path is:

```text
VOROTEX Export
    -> local configurator
    -> edited .Macro.Config / .KB.Config
    -> official VOROTEX Import
    -> K15
```

See [`app/README.md`](app/README.md) for current scope and limitations.

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

## K15 V1 release quick start

The primary installation path is the official VOROTEX `.KB.Config` Import
workflow. With the VOROTEX software open and the K15 connected:

1. Import `K15_VIBECODING_PROFILE_A_TOOLS_AUTH_V1.KB.Config`.
2. Import `K15_VIBECODING_PROFILE_B_MAIN_V1.KB.Config`.
3. Assign the two hardware profile slots as appropriate in the native GUI.
4. Configure the owner-specific Windows selectors: RU = `Ctrl+Shift+2`, EN =
   `Ctrl+Shift+1`.
5. Verify the physical keys. Ordinary text macros use 5 ms event timing and
   append one ASCII space; clipboard macros use `Ctrl+V` followed by safe
   Shift+Enter.

Profile A is the tools/authorization layer; Profile B is the main vibecoding
layer. Profile A key `0` uses the opening-fence-only report-from-clipboard flow
for ChatGPT Web/Codex. Physical Enter emits Shift+Enter; joystick click is
native Enter/Send; Profile A encoder rotation is vertical scroll. The `.KB.Config`
packages preserve each profile's native 14-record lighting bank.

VOROTEX Import is non-pruning: repeated imports can leave duplicate or stale
macro groups. Remove duplicates only through a deliberate native GUI workflow;
do not assume Import state is deterministic from file bytes alone.

### Final V1 key maps

| Key | Profile A — TOOLS_AUTH | Profile B — MAIN_VIBECODING |
|---|---|---|
| 1–6 | Copy, Paste+newline, Cut, Undo, Redo, Select all | Проверь, Следующий шаг, Пиши следующий промпт для агента, Исправляй, Публикуй, Мержи |
| 7–0 | Отчет, Вот отчет, ``` fence, report from clipboard | Создавай, Продолжай, Проведи ревью, Готово |
| . | Дай статус | Дай статус |
| Enter | Shift+Enter / newline | Shift+Enter / newline |
| - / + | Стоп / Подготовь отчет для следующего чата | Стоп / Принимается |
| Space | Подтверждаю | Давай дальше, без push/merge |

Profile A key `0` opens a code block, pastes the clipboard, adds a safe
Shift+Enter, and leaves the caret in the composer; it does not close the fence
or submit. Joystick click is the explicit Send control.

## Design reference

The vibecoding UX is informed by the OpenAI + Work Louder **Codex Micro** control philosophy: workflow actions on a joystick, frequently used commands on dedicated keys, reasoning control on a rotary encoder, and status feedback through lighting.

Reference: https://openai.com/supply/co-lab/work-louder/

This repository is an independent community project and is not affiliated with or endorsed by VOROTEX, OpenAI, or Work Louder.

## Public-repository safety

Do not commit live VOROTEX installation files, local baselines, device dumps, forensic captures, account data, secrets, or machine-specific paths. Sanitized examples must use explicit `.example.*` names rather than live vendor filenames.

See each device directory for current proof status and unresolved areas.
