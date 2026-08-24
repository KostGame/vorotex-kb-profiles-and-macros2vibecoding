# VOROTEX K15 HUD

Small Windows tray utility for showing the current VOROTEX K15 key map as a dark overlay next to the mouse cursor.

The HUD is intentionally separate from the VOROTEX driver/configurator. It does not write device configuration, inspect the keyboard, or require administrator rights.

## MVP behavior

- starts as a tray application with a dedicated dark/cyan VOROTEX-style tray icon;
- `Ctrl+Alt+K` shows/hides the current profile near the cursor;
- `Ctrl+Alt+P` cycles A -> B -> A and shows the selected profile;
- `Ctrl+Alt+Shift+K` shows both profiles side by side;
- hotkeys are editable in `profiles.json`, so a future K15 binding does not require recompilation;
- overlay is topmost but does not intentionally take keyboard focus;
- overlay is clamped to the working area of the monitor containing the cursor;
- overlay auto-hides after 9 seconds by default;
- clicking the overlay also hides it;
- tray menu can show Profile A, Profile B, both profiles, enable per-user Windows autostart, or exit;
- visible labels can be edited in `profiles.json` without recompiling the application;
- if `profiles.json` is absent or invalid, the application falls back to the accepted K15 V1 A/B map compiled into the executable.

The current labels follow `devices/k15-pro/schema/v1-layout-baseline.json`. The HUD shortens some long phrases for glanceability, but does not change the hardware profile itself.

## Current default hotkeys

The initial F13 experiment was dropped because F13 is not yet available on the owner's current K15 setup.

The desktop-safe defaults are now:

- `Ctrl+Alt+K` — show/hide current HUD profile;
- `Ctrl+Alt+P` — switch the HUD's local A/B profile and show it;
- `Ctrl+Alt+Shift+K` — show both profiles.

These are temporary software-level defaults. Once a physical K15 trigger is accepted, the same chord can be emitted by a K15 macro or replaced in `profiles.json`.

## Run from source

Requires Windows and the .NET 8 SDK.

```text
dotnet run --project hud/Vorotex.K15.Hud.csproj
```

## Publish a portable folder

```text
dotnet publish hud/Vorotex.K15.Hud.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The publish directory contains the executable plus `profiles.json`. Copy both files to any user-writable folder and run `Vorotex.K15.Hud.exe`.

Autostart is opt-in from the tray menu and uses the current user's `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run` entry. No service or scheduled task is installed.

## Configuration

`profiles.json` contains display and HUD-control metadata:

- `autoHideMs`: overlay timeout, `0` disables auto-hide;
- `defaultProfile`: `A` or `B`;
- `hotkeys.toggle`: show/hide current profile;
- `hotkeys.cycleProfile`: switch A/B and show it;
- `hotkeys.showBoth`: show both profile cards;
- `profiles[].title`: visible profile title;
- `profiles[].keys`: visible action labels;
- optional key `accent`: `primary`, `flow`, or `send`.

Hotkey strings accept `Ctrl`, `Alt`, `Shift`, and `Win` modifiers plus a Windows key name, for example `Ctrl+Alt+K` or `Ctrl+Shift+F12`. Restart the HUD after editing the file.

This file is not a VOROTEX import/export package and must never be treated as the source of truth for hardware serialization.

## Current limitations

- the utility cannot detect which hardware profile is active because no stable K15 profile-state API has been proven;
- current profile selection is therefore local HUD state;
- hold-to-peek on key release is not in the MVP because `RegisterHotKey` reports a hotkey press, not the corresponding release;
- there is no signed installer or release package yet;
- physical K15 -> HUD trigger mapping still needs owner acceptance testing.
