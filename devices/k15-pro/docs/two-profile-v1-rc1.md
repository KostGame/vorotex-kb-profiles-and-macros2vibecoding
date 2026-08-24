# K15 two-profile V1 RC

The V1 RC keeps language selection orthogonal to profile role:

| Profile | Role | Space |
|---|---|---|
| A | `TOOLS_AUTH` | `Подтверждаю` |
| B | `MAIN_VIBECODING` | `Давай дальше, без push/merge` |

Profile A is the deliberate utility/authorization layer. Its numeric keys
provide Copy, Paste, Cut, Undo, Redo, Select All, `Отчет`, `Вот отчет`, an
English-layout three-backtick fence, and a composite report-from-clipboard
macro. Profile B retains the physically tested vibecoding commands; its Space
is explicitly safe continuation and is not a generic confirmation.

Both profiles use the observed K15 storage model, including the 15
`MemMacId` allocations, `btn_KBKey_Space`, and the joystick's native
`btn_KBKey_Enter`/`KBKey=40` submit path. The corrected Shift+Enter sequence is
preserved as `[225, 40, 40, 225]` with states `[1, 1, 2, 2]`.

The production event delay is configurable and defaults to 5 ms. Physical
Import evidence proves that a full native-template Profile B package at 5 ms,
including the trailing text suffix, imports successfully. Therefore
`KEY_EVENT_DELAY_MS=5` and `5MS_IMPORT_COMPATIBILITY=PROVEN_POSSIBLE`. A
matching minimal 5 ms package
has also crashed, so Import determinism from file bytes alone is not proven.
One millisecond remains untested and disabled for official packages; lower
values require an explicit research-unsafe override and are never silently
rounded. Selector handling is reported separately and is not silently treated
as key-event timing.

Ordinary text commands are compiled with the semantic `TEXT_COMMAND_SUFFIX =
" "` value. Source phrases remain clean (for example, `Проверь`), while the
generated HID stream ends with exactly one Space event. Structural macros such
as Shift+Enter, shortcuts, raw code fences, and the composite clipboard report
sequence opt out of this suffix.

Profile A key `0` is intentionally a ChatGPT-composer-specific composite: it
types `Вот отчет`, performs Shift+Enter, opens a three-backtick English code
block, performs Shift+Enter, pastes with Ctrl+V, and returns the input layout
to Russian. Every Profile A clipboard paste, including key `0`, immediately
uses Shift+Enter after Ctrl+V. It emits no closing fence and never uses native
Enter, so it cannot submit the composer. This is a deliberate clipboard UX
invariant, not a general Markdown macro pattern.

The generator emits independently importable `.KB.Config` packages and
optional standalone `.Macro.Config` packages. A combined Export-All package is
not emitted in this RC because the sanitized evidence proves only the
`SingleProfile`/profile-count delta, not the complete native object shape.
Unsupported fields are intentionally not guessed.

Profile lighting is now an explicit part of the proven KB package model. The
native full-profile lighting fixture supplies the complete 14-record bank for
Profile A from `KBled[0]` and Profile B from `KBled[1]`; the selected bank is
copied byte-for-byte into each single-profile package. The observed Profile A
record `0` is opaque green (`0xFF00FF00`) at brightness `4`. The generator
does not synthesize or normalize LED records and requires an explicit lighting
fixture when a structural template is used.

The known-good minimal single-profile KB shape is import-compatible. A native
`--kb-template` remains optional when preserving additional device defaults is
useful, but it is not required for import compatibility. The physically
successful Profile B 5 ms package uses the full native-template shape and is
the preferred V1 serialization baseline.

## Import-state log for future owner tests

VOROTEX Import is non-pruning and its result is not yet deterministic from file
bytes alone. Before each owner Import test, record:

- application freshly restarted: YES/NO;
- target profile already exists: YES/NO;
- macro group with the same GUID exists: YES/NO;
- macro group with the same name exists: YES/NO;
- import-file SHA-256;
- result: PASS/CRASH.

Do not automate cleanup of profiles or macro groups. Collision suffixes,
accumulated groups, import order, and stale application state are candidates
for later controlled research, not proven causes.

Runtime packages, semantic maps, manifest, and generation report are written
under the ignored `artifacts/` directory. Physical import remains an owner
action and is not performed by the generator.
