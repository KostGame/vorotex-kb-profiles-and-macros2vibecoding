# VOROTEX K15 HUD

Small Windows tray utility for showing the current VOROTEX K15 key map as a dark overlay.

The HUD is intentionally separate from the VOROTEX driver/configurator. It does not write device configuration, inspect the keyboard, or require administrator rights.

## Current V1.2 RC1 behavior

- starts as a tray application with a dedicated VOROTEX tray icon;
- Profile A is rendered in the established dark red HUD palette;
- Profile B is rendered in the established dark blue HUD palette;
- `Ctrl+Alt+K` shows/hides the current profile;
- `Ctrl+Alt+P` cycles A -> B -> A and shows the selected profile;
- `Ctrl+Alt+Shift+K` shows both profiles side by side;
- hotkeys are editable in `profiles.json`;
- overlay size and position are editable in `profiles.json`;
- size presets are `small`, `medium`, and `large`;
- position presets are `aboveCursor` and `bottomRight`;
- defaults are `medium` + `aboveCursor`;
- `aboveCursor` centers the HUD horizontally above the mouse pointer so it is less likely to cover the current input area; if there is not enough room above, it falls back below the cursor;
- `bottomRight` anchors the HUD to the bottom-right of the working area on the monitor containing the cursor;
- selected size is automatically capped if necessary so the HUD remains inside the monitor working area;
- overlay is topmost but does not intentionally take keyboard focus;
- overlay auto-hides after 9 seconds by default;
- clicking the overlay also hides it;
- explicit multiline HUD labels are preserved and dynamically fitted inside key bounds;
- Profile A shows an encoder badge for vertical scroll;
- tray menu can show Profile A, Profile B, both profiles, enable per-user Windows autostart, or exit;
- if `profiles.json` is absent or invalid, the application falls back to the accepted K15 V1.2 RC1 A/B map plus default overlay settings compiled into the executable.

The physical V1.2 RC1 layout was accepted functionally by the owner. One hardware-only anomaly remains: after import, Profile B lighting was observed as white. The HUD intentionally keeps the established blue Profile B convention until the device-lighting issue is corrected; this does not change the accepted macro map.

## Overlay configuration

The default block in `profiles.json` is:

```json
"overlay": {
  "size": "medium",
  "position": "aboveCursor"
}
```

### Size

- `small` = 78% of the current medium layout;
- `medium` = current accepted HUD size;
- `large` = 125% of the current medium layout.

The entire HUD scales together: window, buttons, spacing, borders, and typography. If the requested preset would not fit on the current monitor, the effective size is reduced just enough to keep the whole overlay visible.

### Position

- `aboveCursor` = horizontally centered above the mouse cursor, with a small gap. If there is not enough space above, the HUD appears below the cursor instead;
- `bottomRight` = bottom-right corner of the current monitor working area, with a small margin from the taskbar/screen edge.

Restart the HUD after changing `profiles.json`.

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
- `Ctrl+Alt+Shift+K` — show both profile cards.

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

## Configuration reference

`profiles.json` contains display and HUD-control metadata:

- `autoHideMs`: overlay timeout, `0` disables auto-hide;
- `defaultProfile`: `A` or `B`;
- `hotkeys.toggle`: show/hide current profile;
- `hotkeys.cycleProfile`: switch A/B and show it;
- `hotkeys.showBoth`: show both profile cards;
- `overlay.size`: `small`, `medium`, or `large`;
- `overlay.position`: `aboveCursor` or `bottomRight`;
- `profiles[].title`: visible profile title;
- `profiles[].color`: HUD palette, currently `red` for A and `blue` for B;
- `profiles[].keys[].action`: canonical semantic meaning of the macro;
- optional `profiles[].keys[].label`: HUD-only text, including explicit `\n` line breaks where useful;
- optional `profiles[].keys[].accent`: `primary`, `flow`, or `send`, rendered as a brighter/darker variant of the profile color.

Unknown `overlay.size` values fall back to `medium`. Unknown `overlay.position` values fall back to `aboveCursor`.

Hotkey strings accept `Ctrl`, `Alt`, `Shift`, and `Win` modifiers plus a Windows key name, for example `Ctrl+Alt+K` or `Ctrl+Shift+F12`. Restart the HUD after editing the file.

This file is not a VOROTEX import/export package and must never be treated as the source of truth for hardware serialization.

## Current limitations

- the utility cannot detect which hardware profile is active because no stable K15 profile-state API has been proven;
- current profile selection is therefore local HUD state;
- configuration is loaded at startup, so edits require restarting the HUD;
- hold-to-peek on key release is not in the MVP because `RegisterHotKey` reports a hotkey press, not the corresponding release;
- Profile B hardware lighting currently has a post-import white-light anomaly while its HUD convention remains blue;
- there is no signed installer or release package yet.
