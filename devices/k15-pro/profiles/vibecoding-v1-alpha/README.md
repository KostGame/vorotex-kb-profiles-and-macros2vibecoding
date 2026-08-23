# VIBECODING v1 alpha for VOROTEX K15 Pro

This is the first installable profile candidate. It intentionally keeps **UI Profile1** as the existing safe/factory-style profile and targets **UI Profile2** for vibecoding.

## Why numeric sentinels

K15 macro playback is HID-key based. Direct Russian text therefore depends on the active keyboard layout. This alpha uses improbable digit-only trigger strings such as `77133701` instead. The Windows dispatcher recognizes the trigger, removes it, and performs the semantic action using Unicode text or an application shortcut.

This also lets the physical profile stay stable while ChatGPT, Codex, an IDE, or another application can interpret the same semantic action differently later.

## Alpha key map

| K15 key | Semantic action | Current alpha behavior |
|---|---|---|
| `1` | `CHECK` | insert `Проверь` |
| `2` | `NEXT` | insert `Следующий шаг` |
| `3` | `AGENT_PROMPT` | insert `Пиши следующий промпт для агента` |
| `4` | `FIX` | insert `Исправляй` |
| `5` | `PUBLISH` | insert `Публикуй` |
| `6` | `MERGE` | insert `Мержи` |
| `7` | `CREATE` | insert `Создавай` |
| `8` | `CONTINUE` | insert `Продолжай` |
| `9` | `REVIEW` | insert `Проведи review` |
| `0` | `TEST` | insert `Запусти тесты` |
| `.` | `STATUS` | insert `Дай статус` |
| `Enter` | `NEW_LINE` | newline without submit |
| `-` | `REJECT_OR_STOP` | insert `Стоп` |
| `+` | `SUBMIT` | temporary submit key until joystick-click storage is solved |
| `Space` | `ACCEPT_OR_APPROVE` | insert `Принимается` |

Consequential commands intentionally **do not submit automatically**. Use the separate `SUBMIT` action.

## OpenAI-inspired control model

OpenAI's Codex Micro uses joystick gestures for common workflows, dedicated command keys for frequent actions, and the rotary encoder for reasoning level. This alpha starts conservatively: the K15 joystick keeps cursor behavior and the encoder keeps volume/profile switching until their physical storage schema is proven. Later revisions can move `SUBMIT` to joystick click and reasoning control to encoder rotation.

Reference: https://openai.com/supply/co-lab/work-louder/

## Installation overview

Prerequisites:

1. VOROTEX K15 Pro working normally with the official VOROTEX application.
2. AutoHotkey v2 for the Windows dispatcher.
3. A backup of the current VOROTEX configuration.

Recommended alpha install:

1. Start `dispatcher/windows/vibecoding-k15.ahk`.
2. Generate the Profile2 macro package from `profile.json` using the K15 generator/research tooling.
3. Close VOROTEX completely before replacing local configuration files.
4. Back up `Profile0.json`, `Profile1.json`, `macroConfig.json`, and `DeviceFeature.ini` outside this repository.
5. Install generated `Profile1.json` and `macroConfig.json` using the procedure in the repository root README.
6. Open VOROTEX with K15 connected.
7. Reassign the affected physical keys through the native VOROTEX GUI. This is the currently proven path that writes assignments to K15 onboard memory.
8. Test each key in Notepad/ChatGPT before relying on it in a consequential workflow.
9. Close VOROTEX and power-cycle K15 to confirm onboard persistence.

The first ten keys are already supported by the research generator's confirmed binding schema. The final five standard keys have confirmed physical storage slots but still require native-GUI binding until generator support for their binding labels is proven.

## Alpha fallback

If the dispatcher is not running, pressing a programmed key will type its numeric sentinel. That is intentional and makes failures visible rather than silently performing the wrong action.

## Rollback

Rollback has two parts:

1. restore the backed-up VOROTEX files while VOROTEX is closed;
2. reopen VOROTEX and reassign the original actions through the native GUI so K15 onboard memory matches the restored files.

Do not use Firmware, Update, Reset, or Restore as part of this workflow.
