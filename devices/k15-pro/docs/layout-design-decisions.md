# Layout design decisions

This file records **why** the current V1 layout looks the way it does. Future redesigns may intentionally overturn a decision, but should do so explicitly rather than accidentally losing its rationale.

## D1 — language is not a hardware profile

Decision:

```text
language != hardware profile
```

Each text macro chooses its own target keyboard layout. Hardware profiles are organized by workflow semantics, not by RU/EN.

Reason: the same workflow layer frequently needs both Russian text and English/structural shortcuts.

## D2 — main layer favors frequent safe actions

Profile B is the default day-to-day layer. Frequent commands should be directly reachable there.

Consequential authorization is intentionally not overloaded onto the most convenient key.

## D3 — continuation and confirmation are different intents

Accepted split:

```text
Profile B Space -> Давай дальше, без push/merge 
Profile A Space -> Подтверждаю 
```

Reason: a generic confirmation on the main layer can unintentionally authorize an operation when the intended meaning is merely “continue”.

Any future shortening of these phrases should preserve the semantic distinction.

## D4 — New line and Send are separate physical controls

```text
physical Enter -> Shift+Enter
joystick click -> native Enter / Send
```

Reason: long architect/agent prompts are composed multiline. Submission must remain deliberate and physically distinct.

## D5 — ordinary text commands end with one Space

Decision:

```text
TEXT_COMMAND_SUFFIX = " "
AUTO_PUNCTUATION = false
```

Reason: most macro phrases are usable both as complete short instructions and as prefixes for more typed/pasted context. A trailing period would produce awkward compositions such as `Проверь. этот diff`.

## D6 — clipboard Paste leaves a safe new line

For K15 clipboard semantic macros:

```text
Ctrl+V -> Shift+Enter
```

Reason: after inserting an agent report or other clipboard content, continued typing normally starts on the next line. Native Enter is avoided because it may submit.

## D7 — report-from-clipboard uses opening fence only

Profile A key `0` uses:

```text
Вот отчет
opening ```
Paste
safe new line
stop
```

with no explicit closing fence and no Send.

Reason: this is the cross-client-compatible behavior physically observed in both ChatGPT Web and Codex rich-text composers. The explicit closing-fence workflow could move pasted content outside the code block in Web.

## D8 — shared/global actions keep positions across profiles

Where practical, the following controls remain semantically stable across A/B:

- `.` status;
- `-` stop;
- `+` next-chat report;
- physical Enter new line;
- joystick click Send.

Reason: reduce cognitive switching cost and preserve muscle memory.

## D9 — Profile A encoder is vertical scroll

Accepted Profile A mapping:

```text
encoder up   -> scroll up
encoder down -> scroll down
```

Reason: scrolling is a frequent utility action while inspecting long chats/reports and does not consume a macro key.

This does not automatically imply the same mapping for Profile B.

## D10 — joystick directions are deliberately deferred

Decision: do not assign new semantics yet.

Reason: the best use should come from actual workflow frequency rather than filling unused controls for completeness. Candidates include navigation, tabs, Undo/Redo and workflow-level actions.

## D11 — 5 ms is the V1 release default, not a universal hardware constant

The physically accepted packages work at `5 ms`.

Keep the setting configurable. Do not interpret one successful configuration as proof that every host/firmware/import state can use the same minimum.

## D12 — preserve native peripheral banks instead of reconstructing them

Lighting and unrelated native fields are preserved from accepted profile state.

Reason: VOROTEX has undocumented semantics, including an observed UI/device RGB channel anomaly. Loss-preserving generation is safer than “clean” reconstruction from guesses.

## D13 — official Import is the MVP installation boundary

Prefer generated official `.KB.Config` packages and VOROTEX Import over direct mutation of installed vendor files or low-level device writes.

Reason: it is physically proven, user-understandable, and keeps the first product version independent of extra drivers/services.

## D14 — non-pruning behavior is a UX constraint

Repeated VOROTEX imports can leave old macro groups.

Future installer/configurator work must account for this without silently deleting user state. Cleanup requires a separately designed and approved workflow.
