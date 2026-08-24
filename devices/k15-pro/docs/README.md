# K15 Pro documentation index

Use this directory as the working knowledge base for K15 layout development.

## Canonical current baseline

These documents contain the latest physically validated layout facts and should win if an older draft conflicts with them:

- [`layout-development-baseline.md`](layout-development-baseline.md) — consolidated hardware/UX/serialization baseline.
- [`profile-a-tools-auth.md`](profile-a-tools-auth.md) — accepted Profile A map and clipboard/report semantics.
- [`profile-b-main-vibecoding.md`](profile-b-main-vibecoding.md) — accepted Profile B map and safety separation.
- [`layout-design-decisions.md`](layout-design-decisions.md) — rationale behind the current two-profile UX and safety choices.
- [`native-vorotex-findings.md`](native-vorotex-findings.md) — import/export, macro, timing, lighting, encoder and statefulness findings.
- [`../fixtures/text-macros/README.md`](../fixtures/text-macros/README.md) — sanitized TMAC-001A generated standalone macro import canary and acceptance matrix.
- [`layout-change-protocol.md`](layout-change-protocol.md) — safe procedure for changing a layout and validating it.
- [`research-backlog.md`](research-backlog.md) — intentionally unresolved/deferred research.
- [`../schema/v1-layout-baseline.json`](../schema/v1-layout-baseline.json) — machine-readable V1 layout snapshot for tools/agents.

## Earlier foundation documents

These files remain useful background, but some statements predate the later physical V1 acceptance work:

- [`architecture.md`](architecture.md)
- [`physical-layout.md`](physical-layout.md)
- [`vibecoding-v1.md`](vibecoding-v1.md)

When they conflict with the canonical baseline above, treat the canonical baseline as authoritative until the older file is explicitly refreshed.

## Evidence discipline

Do not collapse these evidence levels:

- **PHYSICALLY PROVEN** — observed on real K15 / official VOROTEX flow.
- **NATIVE EXPORT PROVEN** — proven from official export structure.
- **STRUCTURALLY VALIDATED** — generated/offline validated only.
- **UNRESOLVED** — must not be inferred.

## Public-repository boundary

This documentation is intentionally sanitized. Raw personal exports, local installation paths, accumulated forensic macro groups, credentials, firmware/raw dumps and machine-specific backups do not belong in this public repository.
