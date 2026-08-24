# Profile A — TOOLS / AUTH

Status: **physically accepted V1 layout**.

Profile A is the utility / deliberate-authorization layer. It keeps high-risk confirmation away from the main vibecoding layer and concentrates clipboard/editing/report workflows in one place.

## Physical map

| Control | Action | Implementation notes |
|---|---|---|
| `1` | Copy | `Ctrl+C` |
| `2` | Paste + safe new line | `Ctrl+V`, then `Shift+Enter` |
| `3` | Cut | `Ctrl+X` |
| `4` | Undo | `Ctrl+Z` |
| `5` | Redo | `Ctrl+Shift+Z` |
| `6` | Select All | `Ctrl+A` |
| `7` | `Отчет ` | ordinary RU text command, trailing Space |
| `8` | `Вот отчет ` | ordinary RU text command, trailing Space |
| `9` | three ASCII backticks | structural helper, no suffix/newline/submit |
| `0` | Report from clipboard | composite cross-client workflow, described below |
| `.` | `Дай статус ` | ordinary RU text command |
| physical Enter | New line | `Shift+Enter` |
| `-` | `Стоп ` | ordinary RU text command |
| `+` | `Подготовь отчет для следующего чата ` | ordinary RU text command |
| Space | `Подтверждаю ` | deliberate confirmation, ordinary RU text command |
| joystick click | Send | native Enter, `KBKey=40` |

## A0 — REPORT_FROM_CLIPBOARD

This workflow was physically tested in ChatGPT Web and Codex. The canonical V1 sequence uses an **opening fence only** because that is the compatible intersection of both rich-text composers.

```text
SELECT_RU
TEXT("Вот отчет")
SHIFT_ENTER
SELECT_EN
TEXT("```")
SHIFT_ENTER
CTRL_V
SHIFT_ENTER
SELECT_RU
END
```

Required invariants:

- opening triple-backtick fence: yes;
- explicit closing fence: no;
- pasted content stays inside the code block in the accepted web workflow;
- safe new line after Paste: yes;
- automatic submit: no;
- ordinary text suffix is not injected into structural boundaries.

The explicit Send action remains joystick click.

## Clipboard invariant

For K15 semantic macros, clipboard Paste defaults to:

```text
CTRL_V
SHIFT_ENTER
```

This applies to key `2` and to the Paste stage inside key `0`.

It does **not** imply that every application-level Paste everywhere must add a newline. It is the intentional V1 behavior of these K15 macros for ChatGPT/Codex composition.

## Text-command suffix

Ordinary text commands are stored semantically without trailing whitespace. The compiler adds exactly one ASCII Space:

```text
textCommandSuffix = " "
autoPunctuation = false
```

Structural shortcuts and composites do not receive this suffix automatically.

## Encoder and lighting

Current accepted Profile A peripheral state:

```text
btn_KB_Scr_Up0 = 304     # vertical scroll up
btn_KB_Scr_Dn0 = 305     # vertical scroll down
```

Profile A lighting must be preserved from the accepted native lighting bank rather than regenerated from interpreted RGB values.

A VOROTEX UI selection visually labelled `#00FF00` was observed to drive a physically red result on the keyboard. Therefore native serialized color values are opaque until channel ordering is separately proven.

## Safety intent

The important UX separation is:

```text
Profile B Space -> safe continuation without push/merge authorization
Profile A Space -> explicit "Подтверждаю "
```

Do not collapse these back into one generic confirmation command without a new usability decision.

## Acceptance checklist after any Profile A edit

At minimum re-test:

1. official `.KB.Config` Import does not crash;
2. `0` creates `Вот отчет`, code block, Paste, and leaves a new line without submitting;
3. `2` Pastes and leaves a new line without submitting;
4. Space types `Подтверждаю `;
5. physical Enter is safe new line;
6. joystick click submits;
7. encoder scrolls vertically;
8. lighting remains unchanged by Import.
