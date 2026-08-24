# VOROTEX K15 HUD

Small Windows tray utility for showing the current VOROTEX K15 key map as a dark overlay next to the mouse cursor.

The HUD is intentionally separate from the VOROTEX driver/configurator. It does not write device configuration, inspect the keyboard, or require administrator rights.

## Current V1.2 RC1 behavior

- starts as a tray application with a dedicated VOROTEX tray icon;
- Profile A is rendered in the established dark red HUD palette;
- Profile B is rendered in the established dark blue HUD palette;
- `Ctrl+Alt+K` shows/hides the current profile near the cursor;
- `Ctrl+Alt+P` cycles A -> B -> A and shows the selected profile;
- `Ctrl+Alt+Shift+K` shows both profiles side by side;
- hotkeys are editable in `profiles.json`;
- overlay is topmost but does not intentionally take keyboard focus;
- overlay is clamped to the working area of the monitor containing the cursor;
- overlay auto-hides after 9 seconds by default;
- clicking the overlay also hides it;
- multiline action labels are supported again;
- explicit line breaks in HUD labels are preserved;
- long labels are wrapped and dynamically fitted inside the key bounds;
- Profile A shows an encoder badge for vertical scroll;
- tray menu can show Profile A, Profile B, both profiles, enable per-user Windows autostart, or exit;
- if `profiles.json` is absent or invalid, the application falls back to the accepted K15 V1.2 RC1 A/B map compiled into the executable.

The physical V1.2 RC1 layout was accepted functionally by the owner. One hardware-only anomaly remains: after import, Profile B lighting was observed as white. The HUD intentionally keeps the established blue Profile B convention until the device-lighting issue is corrected; this does not change the accepted macro map.

## V1.2 RC1 profile map

### Profile A · TOOLS / AUTH · red

- `1` COPY
- `2` PASTE + новая строка
- `3` CUT
- `4` UNDO
- `5` REDO
- `6` SELECT ALL
- `7` Отчет
- `8` Вот отчет
- `9` code fence ```
- `0` Отчет из буфера
- `.` Дай статус: что сделано, что осталось, блокеры и следующий шаг
- `Enter` Новая строка (Shift+Enter)
- `-` Стоп
- `+` Подготовь отчет для следующего чата
- `Space` Подтверждаю
- joystick click: ОТПРАВИТЬ
- encoder: вертикальный скролл

### Profile B · MAIN / VIBECODING · blue HUD convention

- `1` Проверь
- `2` Следующий шаг
- `3` Пиши следующий промпт для агента
- `4` Исправляй
- `5` Публикуй
- `6` Мержи
- `7` Создавай
- `8` Продолжай
- `9` Проведи ревью
- `0` Готово
- `.` Дай статус
- `Enter` Новая строка (Shift+Enter)
- `-` Стоп
- `+` Принимается
- `Space` Давай дальше, без push/merge
- joystick click: ОТПРАВИТЬ

The compact HUD labels may use deliberate line breaks or slight wording compression where needed, while `action` retains the canonical semantic text.

## Current default hotkeys

The desktop-safe defaults are:

- `Ctrl+Alt+K` — show/hide current HUD profile;
- `Ctrl+Alt+P` — switch the HUD's local A/B profile and show it;
- `Ctrl+Alt+Shift+K` — show both profiles.

These are software-level defaults. Once a physical K15 trigger is accepted, the same chord can be emitted by a K15 macro or replaced in `profiles.json`.

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
- `profiles[].color`: HUD palette, currently `red` for A and `blue` for B;
- `profiles[].keys[].action`: canonical semantic meaning of the macro;
- optional `profiles[].keys[].label`: HUD-only text, including explicit `\n` line breaks where useful;
- optional `profiles[].keys[].accent`: `primary`, `flow`, or `send`, rendered as a brighter/darker variant of the profile color.

Hotkey strings accept `Ctrl`, `Alt`, `Shift`, and `Win` modifiers plus a Windows key name, for example `Ctrl+Alt+K` or `Ctrl+Shift+F12`. Restart the HUD after editing the file.

This file is not a VOROTEX import/export package and must never be treated as the source of truth for hardware serialization.

## Current limitations

- the utility cannot detect which hardware profile is active because no stable K15 profile-state API has been proven;
- current profile selection is therefore local HUD state;
- hold-to-peek on key release is not in the MVP because `RegisterHotKey` reports a hotkey press, not the corresponding release;
- Profile B hardware lighting currently has a post-import white-light anomaly while its HUD convention remains blue;
- there is no signed installer or release package yet.
