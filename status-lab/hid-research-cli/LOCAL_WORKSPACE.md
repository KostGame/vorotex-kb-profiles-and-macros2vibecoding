# Local research workspace

This file defines the repository-side contract for a local Codex/engineer workspace. It intentionally contains **no implementation task prompt**.

## Repository branch

Implementation branch:

`research/k15-hid-current`

Replacement PR: `#76` for `K15-HID-CONSOLIDATE-001` (architect re-inspection pending).
This is a clean current-main implementation line; it supersedes the historical stacked identity dependency and retains only the minimum static analyzer closure required by the current production authority.

## Recommended local layout

Keep the Git worktree, OEM inputs, and generated output separated:

```text
<workspace-root>/
  repo/
  inputs/
    VOROTEX-K15-PRO.exe
    SXS-W909.exe
  out/
```

The `inputs/` and `out/` directories should live outside the Git worktree whenever practical. Repository-local `.research-local/` is ignored as a fallback scratch location.

## Local research state

At this local research stage:

- create/clone the repository or isolated worktree;
- verify the CLI project builds;
- place or identify the two OEM binaries locally;
- keep SleepTime provenance analysis static/read-only and evidence-gated;
- do not perform HID/device interaction;
- historical stacked HID/identity PRs are research history/authority only; normal development continues from the current-main clean consolidation, and they must not be merged into current main.

The local CLI loop now includes the implemented `sleep-payload-source` analyzer. It may only read the supplied OEM byte images and write local JSON/TXT evidence; it must not open HID devices or launch OEM applications.
