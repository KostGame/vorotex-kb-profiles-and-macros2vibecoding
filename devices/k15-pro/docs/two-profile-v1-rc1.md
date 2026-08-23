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

The production candidate event delay is configurable and defaults to 5 ms.
Generation at 1 ms is supported by `--event-delay-ms 1`; selector handling is
reported separately and is not silently treated as key-event timing.

Ordinary text commands are compiled with the semantic `TEXT_COMMAND_SUFFIX =
" "` value. Source phrases remain clean (for example, `Проверь`), while the
generated HID stream ends with exactly one Space event. Structural macros such
as Shift+Enter, shortcuts, raw code fences, and the composite clipboard report
sequence opt out of this suffix.

The generator emits independently importable `.KB.Config` packages and
optional standalone `.Macro.Config` packages. A combined Export-All package is
not emitted in this RC because the sanitized evidence proves only the
`SingleProfile`/profile-count delta, not the complete native object shape.
Unsupported fields are intentionally not guessed.

An import-compatible `.KB.Config` must be generated with a read-only,
known-good native `--kb-template`. The serializer preserves the template's
full `FnKey`, `FnKeyMacro`, `KBKey`, `KBKeyMacro`, `KBled`, and `KBmain`
sections while replacing only the proven macro bindings and embedded group.
It fails closed instead of emitting the JSON-parseable but incomplete minimal
KB shape that VOROTEX Import rejected.

Runtime packages, semantic maps, manifest, and generation report are written
under the ignored `artifacts/` directory. Physical import remains an owner
action and is not performed by the generator.
