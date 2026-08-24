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
- the tray menu contains separate **Размер** and **Расположение** submenus;
- size choices: **Очень маленький**, **Маленький**, **Средний**, **Большой**;
- position choices: **Над курсором** plus all four monitor corners;
- the selected size and position are applied immediately;
- tray-selected preferences persist after restart;
- defaults remain **Средний + Над курсором**;
- overlay is topmost but does not intentionally take keyboard focus;
- overlay auto-hides after 9 seconds by default;
- clicking the overlay also hides it;
- explicit multiline HUD labels are preserved and dynamically fitted inside key bounds;
- Profile A shows an encoder badge for vertical scroll;
- tray menu can show Profile A, Profile B, both profiles, enable per-user Windows autostart, or exit.

The physical V1.2 RC1 layout was accepted functionally by the owner. One hardware-only anomaly remains: after import, Profile B lighting was observed as white. The HUD intentionally keeps the established blue Profile B convention until the device-lighting issue is corrected; this does not change the accepted macro map.

## Window settings from the tray

Right-click the tray icon.

### Размер

- **Очень маленький** = 62% of medium;
- **Маленький** = 78% of medium;
- **Средний** = accepted current HUD size;
- **Большой** = 125% of medium.

The entire HUD scales together: window, buttons, spacing, borders, and typography. If the selected size would not fit on the current monitor, the effective size is capped so the whole overlay remains visible.

### Расположение

- **Над курсором** = horizontally centered above the mouse cursor with a small gap; if there is not enough room above, it falls back below the cursor;
- **Левый верхний угол**;
- **Правый верхний угол**;
- **Левый нижний угол**;
- **Правый нижний угол**.

Corner positions use the working area of the monitor containing the cursor, so the HUD does not intentionally overlap the Windows taskbar.

### Persistence

Tray selections are stored per Windows user in:

```text
%LOCALAPPDATA%\VOROTEX\K15 HUD\settings.json
```

They survive application and Windows restarts.

The `overlay` block in `profiles.json` provides defaults for a user who does not yet have persisted tray settings:

```json
"overlay": {
  "size": "medium",
  "position": "aboveCursor"
}
```

Once a tray preference has been saved, the per-user setting overrides these defaults.

## Configuration values

Supported `overlay.size` values:

- `extraSmall`
- `small`
- `medium`
- `large`

Supported `overlay.position` values:

- `aboveCursor`
- `topLeft`
- `topRight`
- `bottomLeft`
- `bottomRight`

Unknown values fall back to `medium` and `aboveCursor`.

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

- `Ctrl+Alt+K` — show/hide current HUD profile;
- `Ctrl+Alt+P` — switch the HUD's local A/B profile and show it;
- `Ctrl+Alt+Shift+K` — show both profile cards.

Hotkeys remain editable in `profiles.json`.

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

Autostart is opt-in from the tray menu and uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry. No service or scheduled task is installed.

This file is not a VOROTEX import/export package and must never be treated as the source of truth for hardware serialization.

## Current limitations

- the utility cannot detect which hardware profile is active because no stable K15 profile-state API has been proven;
- current profile selection is therefore local HUD state;
- hold-to-peek on key release is not in the MVP because `RegisterHotKey` reports a hotkey press, not the corresponding release;
- Profile B hardware lighting currently has a post-import white-light anomaly while its HUD convention remains blue;
- there is no signed installer or release package yet.
