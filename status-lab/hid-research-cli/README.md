# VOROTEX K15 HID Research CLI

Developer-only headless entry point for the static analyzers used by `Vorotex.K15.HidResearchLab`.

This project is **not a fifth product app**. It exists so a local Codex/engineer can iterate on reverse-engineering analyzers without rebuilding and downloading the GUI artifact after every change.

## Safety boundary

The CLI is static/read-only by design:

- no HID/device open;
- no `HidD_SetFeature` / `HidD_GetFeature` execution;
- no report replay;
- no OEM process launch/attach/injection/debugger;
- no executable/resource patching;
- no driver/registry/profile/keymap/macro/lighting/sleep/firmware mutation.

It only reads the selected OEM executable files and writes JSON/TXT analysis output.

## Invocation contract

```text
Vorotex.K15.HidResearch.Cli --mode <mode> --a <VOROTEX-K15-PRO.exe> --b <SXS-W909.exe> --out <directory>
Vorotex.K15.HidResearch.Cli --list-modes
```

Implemented modes:

- `sleep-report`
- `sleep-report-construction`
- `sleep-payload-seed`
- `sleep-payload-helper-semantics`
- `sleep-payload-source`

The `sleep-payload-source` mode is implemented as a static, read-only provenance analyzer. Its verdict remains capped below a SleepTime field proof unless an explicit, complete SleepTime-to-source-byte-to-report-to-SetFeature chain is proven.

## Local workspace convention

Keep OEM binaries and generated output **outside the Git worktree**. Recommended layout:

```text
<workspace-root>/
  repo/                 # clone/worktree for this repository
  inputs/
    VOROTEX-K15-PRO.exe
    SXS-W909.exe
  out/
```

Do not copy raw OEM binaries into tracked repository paths.

For Codex iteration, the intended loop is:

```text
edit analyzer -> dotnet build CLI -> run CLI against local inputs -> inspect JSON/TXT -> refine -> repeat
```

GitHub artifacts remain useful for independent owner validation, but they are no longer required for every static-analysis iteration.
