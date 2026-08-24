# Profile B — MAIN / VIBECODING

Status: **physically accepted V1 layout**.

Profile B is the primary day-to-day architect / coding layer. It favors frequent safe commands and keeps consequential confirmation on Profile A.

## Physical map

| Control | Action |
|---|---|
| `1` | `Проверь ` |
| `2` | `Следующий шаг ` |
| `3` | `Пиши следующий промпт для агента ` |
| `4` | `Исправляй ` |
| `5` | `Публикуй ` |
| `6` | `Мержи ` |
| `7` | `Создавай ` |
| `8` | `Продолжай ` |
| `9` | `Проведи ревью ` |
| `0` | `Готово ` |
| `.` | `Дай статус ` |
| physical Enter | `Shift+Enter` / safe new line |
| `-` | `Стоп ` |
| `+` | `Подготовь отчет для следующего чата ` |
| Space | `Давай дальше, без push/merge ` |
| joystick click | native Enter / Send |

All ordinary text commands receive exactly one trailing ASCII Space and no automatic punctuation.

## Why Space is safe continuation

A generic `Подтверждаю` on the main layer created an authorization ambiguity: continuation and consequential approval are not the same intent.

The accepted split is therefore:

```text
Profile B Space -> Давай дальше, без push/merge 
Profile A Space -> Подтверждаю 
```

This makes the highest-frequency continuation action easy while requiring an intentional profile switch for explicit confirmation.

## New line and submit

These are intentionally separate physical actions:

```text
physical Enter  -> Shift+Enter -> compose another line
joystick click  -> native Enter -> submit/send
```

No text command should auto-submit.

## Language behavior

Russian commands force the configured RU input profile before emitting the Russian-layout HID sequence.

Accepted selector configuration for the current profile set:

```text
RU -> Ctrl+Shift+2
EN -> Ctrl+Shift+1
```

The selectors are configurable host assumptions, not universal Windows defaults.

## Timing

The accepted full-template Profile B package physically imported and worked with:

```text
KEY_EVENT_DELAY_MS = 5
```

A 5 ms value must not be described as universally safe for every VOROTEX state or firmware version. It is physically proven possible for the accepted package. Keep the delay configurable for future compatibility work.

## Encoder, joystick directions and lighting

- joystick click is proven native Enter / Send;
- joystick directions remain unchanged from the accepted native profile and are reserved for later layout experiments;
- Profile B encoder behavior should remain unchanged unless a separate physical fixture proves an intentional new mapping;
- Profile B lighting must be preserved from its accepted native lighting bank when the package is regenerated.

## Acceptance checklist after any Profile B edit

At minimum re-test:

1. official `.KB.Config` Import succeeds;
2. one short RU command and one long RU command type without missing characters;
3. final trailing Space is present;
4. starting from EN still produces correct Russian text;
5. physical Enter creates a new line only;
6. joystick click submits;
7. Space produces the safe continuation phrase exactly;
8. unrelated encoder, joystick-direction and lighting behavior remains unchanged.
