# VOROTEX K15 HUD

Small Windows tray utility for showing the current VOROTEX K15 key map as a dark overlay next to the mouse cursor.

The HUD is intentionally separate from the VOROTEX driver/configurator. It does not write device configuration, inspect the keyboard, or require administrator rights.

## MVP behavior

- starts as a tray application;
- `F13` shows/hides the current profile near the cursor;
- `Shift+F13` cycles A -> B -> A and shows the selected profile;
- `Ctrl+F13` shows both profiles side by side;
- overlay is topmost but does not intentionally take keyboard focus;
- overlay is clamped to the working area of the monitor containing the cursor;
- overlay auto-hides after 9 seconds by default;
- clicking the overlay also hides it;
- tray menu can show Profile A, Profile B, both profiles, enable per-user Windows autostart, or exit;
- `profiles.json` can change the visible labels without recompiling the application;
- if `profiles.json` is absent or invalid, the application falls back to the accepted K15 V1 A/B map compiled into the executable.

The current labels follow `devices/k15-pro/schema/v1-layout-baseline.json`. The HUD shortens some long phrases for glanceability, but does not change the hardware profile itself.

## Suggested K15 binding

For the first physical setup, dedicate one otherwise-unused control to the HUD hotkey. `F13` is a convenient choice if the native VOROTEX UI for that control can emit it reliably. If that is not available on the tested device/software combination, keep the utility on a normal Windows hotkey until a hardware-safe trigger is proven.

The HUD itself does not assume that any particular joystick direction is available. Joystick-direction semantics are still an open item in the V1 device baseline.

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

`profiles.json` contains only display metadata for the HUD:

- `autoHideMs`: overlay timeout, `0` disables auto-hide;
- `defaultProfile`: `A` or `B`;
- `profiles[].title`: visible profile title;
- `profiles[].keys`: visible action labels;
- optional key `accent`: `primary`, `flow`, or `send`.

This file is not a VOROTEX import/export package and must never be treated as the source of truth for hardware serialization.

## Current limitations

- the utility cannot detect which hardware profile is active because no stable K15 profile-state API has been proven;
- current profile selection is therefore local HUD state;
- hold-to-peek on key release is not in the MVP because `RegisterHotKey` reports a hotkey press, not the corresponding release;
- there is no signed installer or release package yet;
- physical K15 -> HUD trigger mapping still needs owner acceptance testing.
