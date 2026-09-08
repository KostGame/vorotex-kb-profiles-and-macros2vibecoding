# vNext side-by-side package contract

`New-VNextPackage.ps1` produces a disposable staging package without touching the owner host or the legacy install. `Apply-VNextPackage.ps1` is the repo-only apply/update/rollback path and accepts an explicit temporary install root for tests.

```text
<package>/
  current-version.txt
  manifest.json
  integration/             # stable bridge/config integration boundary
  data/                    # persistent mutable runtime data boundary
  versions/<version>/payload/
    Vorotex.K15.Runtime.exe
    Vorotex.K15.StatusTray.exe
    Vorotex.K15.ControlCenter.exe
    Vorotex.K15.LiveDashboard.exe
```

`current-version.txt` is the only selection mechanism. An update writes a complete new immutable version, validates its per-version manifest, then atomically replaces this small selection file using a same-volume temporary file. Rollback selects the previous version; it never copies mutable `data` backwards. The package root is distinct from `%LOCALAPPDATA%\VorotexK15\app`. Phase A makes no autostart, registry, hook, bridge, HID, or legacy-install changes.
