# Layout change protocol

Use this protocol for future K15 profile/layout work. The goal is to change semantics quickly without repeatedly rediscovering VOROTEX storage behavior or accidentally regressing physically accepted controls.

## 1. Start from the canonical baseline

Read first:

- [`layout-development-baseline.md`](layout-development-baseline.md)
- the affected profile document;
- [`native-vorotex-findings.md`](native-vorotex-findings.md)
- [`research-backlog.md`](research-backlog.md) if touching unresolved controls.

Do not start from a random native re-export or an old experimental package.

## 2. Describe the change semantically first

For each control being changed, record:

```text
physical control
current semantic
new semantic
target language: RU / EN / none
text vs shortcut vs structural composite
submit behavior: never / explicit only
```

Only then map it to VOROTEX fields/HID events.

## 3. Preserve unrelated proven state

A layout edit must not accidentally alter:

- other physical bindings;
- joystick click native Enter;
- joystick directions unless explicitly in scope;
- encoder behavior outside the target profile;
- lighting banks;
- language selector configuration;
- macro timing policy;
- macro GUID/group relationships unrelated to the edit.

If a native export is used as evidence, copy only the proven delta unless the whole profile bank is intentionally the baseline.

## 4. Text macro rules

Default ordinary text command:

```text
semantic source string has no trailing whitespace
compiler selects target language
compiler emits text
compiler appends exactly one ASCII Space
no automatic punctuation
no automatic submit
```

Structural macros are exceptions and must define their exact sequence explicitly.

## 5. Clipboard rules

Current V1 semantic Paste invariant:

```text
CTRL_V
SHIFT_ENTER
```

Use native Enter only for explicit Send, not as the post-Paste newline.

The accepted Profile A report-from-clipboard composite is documented in [`profile-a-tools-auth.md`](profile-a-tools-auth.md).

## 6. Newline vs Send

Never blur these actions:

```text
physical Enter -> Shift+Enter -> safe new line
joystick click -> native Enter -> Send
```

A new text macro must not auto-submit unless a future design explicitly introduces a separate high-confidence action and physically validates it.

## 7. Language compilation

Text language is independent of the hardware profile.

For each text macro:

1. select configured RU/EN input profile if required;
2. emit physical HID usages for that layout;
3. preserve the corrected layout map, including `г -> HID 24`;
4. keep selector chords configurable.

Do not encode Russian as if K15 can emit arbitrary Unicode directly.

## 8. Generator/static validation

Before physical Import, tests should verify at least:

- every macro-bound key resolves to one embedded group/macro;
- expected `MemMacId` allocation is preserved;
- active event prefixes align with `num`;
- native zero-filled array tails remain compatible;
- `macRpt=1`, `rptType=0` for Cycle=1 macros;
- ordinary text suffix policy;
- structural composites have exact action order;
- no native Enter appears where safe Shift+Enter is required;
- joystick click stays native `KBKey=40`;
- lighting bank remains exact when not intentionally changed;
- encoder fields remain exact when not intentionally changed;
- generated package contains no stale/unrelated macro groups.

A JSON parse alone is not an import-compatibility test.

## 9. Physical acceptance test

For a changed profile, test the smallest useful set after official VOROTEX Import:

1. Import succeeds without crash.
2. Changed control performs the new action.
3. One short text macro works.
4. One long text macro works at current delay.
5. Starting from the opposite keyboard language still produces correct text.
6. Physical Enter creates a new line only.
7. Joystick click submits.
8. Lighting remains expected.
9. Encoder/joystick controls outside scope remain unchanged.

If the edit affects clipboard/rich-text behavior, test in both ChatGPT Web and Codex where relevant.

## 10. Import-crash evidence collection

If Import crashes, do not immediately assign a file-format root cause. Record:

```text
file SHA-256
fresh app restart yes/no
profile already existed yes/no
same group name present yes/no
same group GUID present yes/no
previous import attempted
minimal/full template shape
exact change from last physically accepted package
```

Prefer a one-variable canary over many speculative variants.

## 11. Non-pruning cleanup rule

VOROTEX can accumulate stale groups. Do not automate destructive cleanup as part of a normal layout update.

If a clean-state experiment is required, make it a separate owner-approved procedure and record exactly what is deleted/restored.

## 12. Public repository hygiene

Never commit:

- live files copied from the installed VOROTEX directory;
- personal Downloads/Desktop paths;
- raw forensic exports containing unrelated accumulated groups;
- credentials/tokens;
- firmware/raw dumps with unclear redistribution rights.

Safe public content includes:

- sanitized findings;
- generated portable release packages;
- declarative profile definitions;
- regression fixtures stripped to the minimum needed evidence, when redistribution is appropriate.

## 13. Review and publication

Before merging a layout change:

1. review complete diff against current `main`;
2. run all K15 generator tests;
3. run existing repository/configurator checks too;
4. run whitespace/diff checks;
5. verify public-artifact hygiene;
6. state what was physically tested and what remains only structurally validated;
7. do not silently upgrade `UNRESOLVED` facts to `PROVEN`.

## 14. Change-record template

Use this compact block in future PRs/issues:

```text
CONTROL=<physical control>
OLD=<semantic>
NEW=<semantic>
PROFILE=A|B
LANGUAGE=RU|EN|NONE
STORAGE_FIELD=<field>
MEMMACID=<n or N/A>
TIMING_MS=<n>
STRUCTURAL_TEST=PASS|FAIL
PHYSICAL_IMPORT=PASS|FAIL|NOT_RUN
PHYSICAL_BEHAVIOR=PASS|FAIL|NOT_RUN
UNRELATED_STATE_PRESERVED=PASS|FAIL
```
