# VIBECODING v1

Status: **alpha profile available for testing**.

The goal is a one-hand control surface for ChatGPT, Codex, AgentLoop-style workflows, and general development work while keeping consequential actions deliberate.

The installable alpha specification is in [`../profiles/vibecoding-v1-alpha/`](../profiles/vibecoding-v1-alpha/).

## UX principles

The layout follows four principles inspired by the OpenAI + Work Louder Codex Micro control model:

1. frequent commands live on dedicated keys;
2. workflow-level actions are strong joystick candidates;
3. rotary input is a natural fit for a continuously adjustable setting such as reasoning level;
4. lighting can later expose agent state.

Reference: https://openai.com/supply/co-lab/work-louder/

## Hardware-profile strategy

For the first alpha:

- **UI Profile1** is preserved as the safe/factory-style fallback;
- **UI Profile2** becomes the VIBECODING profile.

The K15 hardware profile remains intentionally simple. Application-specific behavior is moved to a Windows dispatcher so the keyboard does not need to be rewritten every time a ChatGPT/Codex workflow changes.

## VIBECODING alpha key semantics

| Key | Semantic action | Alpha behavior |
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
| `+` | `SUBMIT` | temporary submit key |
| `Space` | `ACCEPT_OR_APPROVE` | insert `Принимается` |

Consequential commands do **not** submit automatically. Text insertion and submission are separate gestures.

## Newline vs submit

A core requirement is to separate multiline composition from submission:

```text
K15 Enter -> NEW_LINE
joystick click -> SUBMIT
```

Joystick-click storage is not yet proven, therefore `+` is the temporary `SUBMIT` key in the alpha. When joystick click becomes programmable, `SUBMIT` moves there and `+` can return to an approve/accept role.

The alpha dispatcher currently sends `Shift+Enter` for `NEW_LINE`. App-specific ChatGPT/Codex/IDE rules are the next calibration step.

## Layout-independent trigger transport

Direct Russian onboard macros are keyboard-layout dependent because K15 macro playback is HID-key based. The alpha therefore programs digit-only sentinels such as `77133701` and lets AutoHotkey v2 translate them to Unicode text or shortcuts.

Benefits:

- EN/RU layout does not change the semantic command;
- long prompts live in software rather than onboard memory;
- the same physical key can later behave differently in ChatGPT, Codex, an IDE, or a terminal;
- if the dispatcher is not running, failure is visible as the raw numeric sentinel.

## Joystick candidate workflows

OpenAI's Codex Micro uses joystick gestures for common workflows such as PR review, debugging, and refactoring. K15 can adopt the same high-level idea after its joystick storage mapping is proven.

Preferred future semantics:

| Direction | Candidate semantic |
|---|---|
| Up | `PR_REVIEW` |
| Down | `DEBUG_OR_FIX` |
| Left | `REFACTOR` |
| Right | `VERIFY_OR_TEST` |
| Click | `SUBMIT` |

Alpha fallback: retain native cursor behavior.

## Encoder candidate behavior

Preferred future AI-profile behavior, after remapping is proven:

- rotate left: reasoning level down;
- rotate right: reasoning level up;
- click: switch hardware profile.

Alpha fallback: retain native volume control and profile switching.

## Application-aware dispatcher

The first dispatcher is [`../dispatcher/windows/vibecoding-k15.ahk`](../dispatcher/windows/vibecoding-k15.ahk).

The alpha maps sentinels to common text/actions. Future rules can specialize the same semantics by foreground application, for example:

```text
CHECK
  ChatGPT -> insert a conversational check instruction
  Codex   -> insert a code-review/check instruction
  IDE     -> run a configured verification action
```

## Future status lighting

If K15 lighting proves externally controllable without unsafe device operations, a later version can map agent state to visual feedback: idle, thinking, running, waiting, done, and blocked.
