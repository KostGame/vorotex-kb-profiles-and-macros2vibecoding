# K15 Pro layout research backlog

This backlog contains intentionally unresolved or deferred topics. Items here must not leak into release claims as if they were already proven.

## P0 — usage-semantics analysis

Goal: optimize future layouts from real conversation behavior rather than intuition alone.

Analyze recent ChatGPT/Codex/architect sessions and collect:

- phrases ChatGPT most often asks the user to confirm/choose/provide;
- phrases the user most often replies with;
- architect-specific continuation/approval/review vocabulary;
- clipboard/report workflows;
- commands that are frequent enough to deserve dedicated hardware positions;
- commands that are rare or risky and should stay behind Profile A or another deliberate layer.

Desired output:

- normalized semantic intents, not just literal phrase counts;
- frequency ranking;
- candidate remaps for A/B;
- conflicts where one key currently serves multiple intents;
- recommendation whether a third logical layer is worth introducing later.

Do not redesign V1 until the analysis is complete enough to justify changes.

## P0 — exact RGB channel mapping

Known anomaly:

```text
UI #00FF00 / R0 G255 B0
-> physical keyboard appears red
```

Perform a controlled three-color native test:

```text
UI FF0000 -> record physical result + serialized native value
UI 00FF00 -> record physical result + serialized native value
UI 0000FF -> record physical result + serialized native value
```

Goal: determine actual channel permutation between UI serialization and device output.

Until then:

- preserve native LED banks verbatim;
- do not swap channels in the generator;
- describe native color values as opaque.

## P1 — joystick directions

Current V1 policy: preserve existing directions.

Candidate future uses to evaluate from actual usage:

- Page Up / Page Down;
- previous/next tab;
- Undo/Redo;
- workflow actions such as review/fix/test/refactor.

Do not assign directions merely because they are available. Prefer the behavior with the highest repeated one-hand value after real usage data is collected.

## P1 — encoder behavior by profile

Profile A vertical scroll is physically proven:

```text
Up   -> 304
Down -> 305
```

Future questions:

- should Profile B remain its current behavior permanently;
- should scrolling be consistent across A/B for muscle memory;
- is reasoning-level control useful enough to justify a host-side dispatcher later;
- can encoder click/profile switching be represented and generated safely without destabilizing accepted profiles.

## P1 — combined two-profile package

Individual one-file single-profile `.KB.Config` installation is proven.

Investigate a final combined `Export all`/multi-profile distribution only from native evidence. Do not guess `SingleProfile=0` structure or merge two files synthetically unless all required fields are proven.

## P1 — importer state dependence

Same/related packages produced both crash and successful outcomes during testing.

Build a controlled matrix that records:

- clean restart;
- existing profile/group state;
- same-name collision;
- same-GUID collision;
- import order;
- minimal vs full shape;
- package SHA-256.

Goal: determine whether crashes are caused by collisions, stale application state, specific structures, or another variable.

## P2 — 1 ms timing canary

Current accepted release default: `5 ms`.

`1 ms` remains intentionally unproven for normal release generation.

If investigated later:

- use one short diagnostic package;
- keep all structure identical to a physically accepted 5 ms baseline;
- vary only active delays;
- test Import and physical typing separately;
- do not promote to default from a single successful phrase.

## P2 — more language profiles

The architecture supports per-macro language selection rather than per-hardware-profile language.

Good candidates:

- German;
- Italian;
- other direct keyboard-layout languages.

IME-heavy languages such as Chinese/Japanese/Korean need separate research because direct physical-key mapping alone may not provide deterministic text output.

## P2 — configurator integration

The repository contains a local VOROTEX configurator. Future work can expose proven semantic controls such as:

- physical K15 layout view;
- text macro editor;
- language selector settings;
- Profile A/B semantic templates;
- encoder fields;
- lighting-bank preservation;
- validation of `MemMacId`/GUID/event-prefix invariants.

Configurator work must remain loss-preserving for unknown native fields.

## P3 — lower-level device apply

Official VOROTEX Import remains the preferred MVP path.

Do not prioritize direct HID/firmware/device-write automation until there is a compelling user benefit and a separately verified safe protocol.
