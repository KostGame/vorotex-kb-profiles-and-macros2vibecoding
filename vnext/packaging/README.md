# vNext side-by-side package contract

`New-VNextPackage.ps1` produces a disposable staging package without touching the owner host or the legacy install. `Apply-VNextPackage.ps1` is the repo-only apply/update/rollback path and accepts an explicit temporary install root for tests.

```text
<package>/
  current-version.txt
  integration/             # stable bridge/config integration boundary
  data/                    # persistent mutable runtime data boundary
  versions/<version>/payload/
    manifest.json
    Vorotex.K15.Runtime.exe
    Vorotex.K15.StatusTray.exe
    Vorotex.K15.ControlCenter.exe
    Vorotex.K15.LiveDashboard.exe
```

`current-version.txt` is the only selection mechanism. Each `versions/<version>/manifest.json` is immutable provenance plus an exact payload inventory; its hashes exclude the selector and mutable `integration`/`data`. An update writes a complete new immutable version, validates it, then atomically replaces this small selector using a same-volume temporary file. Rollback validates the installed target and selects the previous version; it never copies mutable `data` backwards. The package root is distinct from `%LOCALAPPDATA%\VorotexK15\app`. Phase A makes no autostart, registry, hook, bridge, HID, or legacy-install changes.

Provenance semantics are exact: `source.ref` is the source ref used for the build, `source.baseMainSha` is the accepted main/base commit (the PR base SHA for pull requests, or the actual build commit for a main/branch build), and `source.buildCommit` is the checked-out/build commit. The builder requires the base commit to exist in Git and be an ancestor of the build commit.
