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

The production/import-safe event delay is configurable and defaults to 10 ms.
Physical Import evidence proves 10 ms and proves that 5 ms crashes VOROTEX
Import. Therefore `MIN_PROVEN_IMPORT_SAFE_DELAY_MS=10`,
`5MS_IMPORT_COMPATIBILITY=FAIL`, and 1 ms is untested and disabled for
official packages. Lower values require an explicit research-unsafe override;
they are never silently rounded. Selector handling is reported separately and
is not silently treated as key-event timing.

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

The known-good minimal single-profile KB shape is import-compatible. A native
`--kb-template` remains optional when preserving additional device defaults is
useful, but it is not required for import compatibility.

Runtime packages, semantic maps, manifest, and generation report are written
under the ignored `artifacts/` directory. Physical import remains an owner
action and is not performed by the generator.
