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

## Legacy/research direct-file installation

Direct mutation of live VOROTEX installation files is **not** the normal installation path anymore. It remains documented only as a research/recovery technique for old experiments.

The supported K15 profile path is the official VOROTEX `.KB.Config` Import workflow. Generated profile packages carry their embedded macro groups and physical key → macro GUID bindings, so manual replacement of `Profile*.json` and `macroConfig.json` is unnecessary for normal use.

If low-level research against live files is ever required, close VOROTEX first, back up the full configuration, avoid firmware/reset/restore actions, and treat onboard state as separate from local JSON state. VOROTEX Import is non-pruning/state-dependent, so repeated imports may leave duplicate or stale macro groups.

## K15 V1.2 RC1 quick start

The current physically accepted baseline is **V1.2 RC1**. Use the official VOROTEX profile Import workflow with the K15 connected:

1. Import `K15_VIBECODING_PROFILE_A_TOOLS_AUTH_V1_2_RC1.KB.Config`.
2. Import `K15_VIBECODING_PROFILE_B_MAIN_V1_2_RC1.KB.Config`.
3. Assign the two hardware profile slots as appropriate in the native GUI.
4. Configure the owner-specific Windows selectors: RU = `Ctrl+Shift+2`, EN = `Ctrl+Shift+1`.
5. Verify the physical keys after import.

V1.2 timing policy:

- ordinary text HID events: **1 ms**;
- layout selector events: **5 ms**;
- structural/UI-sensitive events: **5 ms**;
- ordinary text commands append exactly one ASCII space;
- no automatic punctuation is added.

Profile A is the tools/authorization layer; Profile B is the main vibecoding layer. Physical Enter emits Shift+Enter; joystick click remains native Enter/Send. Profile A encoder rotation is vertical scroll.

The generated files preserve the serialized lighting banks from the canonical source. In physical owner testing, Profile B appeared with white lighting after VOROTEX Import. This is a known, accepted non-blocking V1.2 RC1 discrepancy; physical lighting parity is not claimed.

VOROTEX Import is non-pruning: repeated imports can leave duplicate or stale macro groups. Remove duplicates deliberately in the native GUI rather than assuming import state is deterministic from file bytes alone.

### Current V1.2 key maps

| Key | Profile A — TOOLS_AUTH | Profile B — MAIN_VIBECODING |
|---|---|---|
| 1 | Copy | Проверь |
| 2 | Paste + Shift+Enter | Следующий шаг |
| 3 | Cut | Пиши следующий промпт для агента |
| 4 | Undo | Исправляй |
| 5 | Redo | Публикуй |
| 6 | Select all | Мержи |
| 7 | Отчет | Создавай |
| 8 | Вот отчет | Продолжай |
| 9 | \`\`\` code fence | Проведи ревью |
| 0 | Report from clipboard | Готово |
| . | Дай статус: что сделано, что осталось, блокеры и следующий шаг | Дай статус |
| Enter | Shift+Enter / newline | Shift+Enter / newline |
| - | Стоп | Стоп |
| + | Подготовь отчет для следующего чата | Принимается |
| Space | Подтверждаю | Давай дальше, без push/merge |
| Joystick click | Native Enter / Send | Native Enter / Send |

Profile A key `0` opens a code block, pastes the clipboard, adds a safe Shift+Enter, and leaves the caret in the composer. It does not close the fence or submit.

Historical V1/V1.1 packages remain in the repository for reproducibility, but V1.2 RC1 is the current canonical K15 vibecoding baseline.

## Design reference

The vibecoding UX is informed by the OpenAI + Work Louder **Codex Micro** control philosophy: workflow actions on a joystick, frequently used commands on dedicated keys, reasoning control on a rotary encoder, and status feedback through lighting.

Reference: https://openai.com/supply/co-lab/work-louder/

This repository is an independent community project and is not affiliated with or endorsed by VOROTEX, OpenAI, or Work Louder.

## Public-repository safety

Do not commit live VOROTEX installation files, local baselines, device dumps, forensic captures, account data, secrets, or machine-specific paths. Sanitized examples must use explicit `.example.*` names rather than live vendor filenames.

See each device directory for current proof status and unresolved areas.
