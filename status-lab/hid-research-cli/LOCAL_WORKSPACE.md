# Local research workspace

This file defines the repository-side contract for a local Codex/engineer workspace. It intentionally contains **no implementation task prompt**.

## Repository branch

Infrastructure branch:

`agent/k15-hid-local-research-cli`

This branch is stacked from the current keyboard SleepTime research branch and is intended to provide the reusable local execution surface only.

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

## Agent preparation state

At this infrastructure stage:

- create/clone the repository or isolated worktree;
- verify the CLI project builds;
- place or identify the two OEM binaries locally;
- do not start the next SleepTime provenance task yet;
- do not perform HID/device interaction;
- do not merge the infrastructure or stacked research PRs.

The later agent task will add/iterate the reserved `sleep-payload-source` analyzer using the local CLI loop.
