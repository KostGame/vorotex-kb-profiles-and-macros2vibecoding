# VOROTEX K15 Status Lab

Windows tray helper и Control Center для RGB-индикации состояний Codex на VOROTEX K15 Pro.

## Текущий baseline

RC1 уже принят в `main`. RC2 развивает его как обычный маленький Windows-продукт, не переписывая физически проверенные HID/state слои.

```text
PROFILE A = RED
PROFILE B = BLUE

цвет   -> какой hardware profile активен
эффект -> что сейчас делает агент
```

`NORMAL` не является notification-effect. Он восстанавливает exact onboard baseline. Status Lab наблюдает физический A/B и никогда не переключает hardware profile программно.

Текущие defaults:

```text
RGB-индикация ON -> быстрый Cycle breathing RED <-> BLUE
RUNNING           -> Flowing Water, цвет активного профиля
WAITING           -> Single-color breathing, speed 7, цвет профиля
STOP event        -> Cycle breathing RED <-> BLUE, короткий overlay
DONE              -> Single-color breathing, speed 5, цвет профиля
NORMAL            -> exact device baseline
ERROR             -> reserved / disabled
profile switch    -> собственная штатная анимация K15; overlay Status Lab default OFF
```

## Источники состояния

Primary semantic source: Codex lifecycle hooks.

```text
UserPromptSubmit  -> RUNNING
PermissionRequest -> WAITING
PreToolUse        -> RUNNING после Approve, когда tool реально стартовал
PostToolUse       -> RUNNING fallback
Stop              -> DONE_PENDING_ATTENTION
SessionEnd        -> завершает только свою session
```

Reducer session-aware: хранит `sessionId`, `turnId`, `cwd`, игнорирует internal `.codex-agentloop\memories` / `.codex\memories` как foreground и переигрывает recent hooks при запуске.

Windows `UserNotificationListener` остаётся supplemental attention channel. Toast keyword heuristics не создают semantic `ERROR`.

### DONE -> NORMAL

Current semantic fallback default = 30 секунд. DONE также завершается при удалении сопоставленного Windows completion-notification или ручным `✓ Сбросить WAITING / DONE`.

Простое открытие/foreground окна Codex **пока не считается acknowledgement**. RC2 сознательно не меняет эту семантику до дополнительного owner-теста.

## RC2 Control Center

Открывается первым пунктом tray или двойным кликом по tray icon.

Control Center показывает:

- текущий normalized state;
- причину последнего перехода;
- сколько времени состояние активно;
- focused Codex session и cwd;
- RGB status;
- Windows notification listener status;
- Codex hooks health;
- detailed logging status;
- Windows autostart status;
- путь/schema текущего config.

Быстрые действия:

- RGB ВКЛ/ВЫКЛ;
- сброс WAITING/DONE;
- exact baseline restore текущего физического профиля;
- install/update Codex hooks;
- Advanced RGB configurator;
- Lighting Lab;
- журнал/диагностика;
- per-user Windows autostart через `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Sleep / standby research

Управление sleep/power K15 ещё не имеет доказанного протокола. Поэтому RC2 **не отправляет неизвестных HID power-команд** и не выдаёт эксперимент за готовую настройку.

В Control Center добавлен evidence-first workflow:

```text
Capture BEFORE
    -> в официальном VOROTEX изменить ТОЛЬКО sleep/standby
Capture AFTER + diff
    -> локальный report
```

Research output хранится только локально:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\device-settings-research\<timestamp>\
```

Он содержит private before/after copies и локальный `report.json`/`report.txt`. Ничего не публикуется автоматически.

Проверяются кандидаты:

```text
DeviceFeature.ini   -> volatile_candidate
KBconfig.ini        -> stable_candidate
Profile0.json       -> stable_candidate
Profile1.json       -> stable_candidate
```

Для JSON report сохраняет изменившиеся JSON paths; для INI line diff. `DeviceFeature.ini` специально помечается volatile, потому что прежние controlled diffs показали его самостоятельные служебные изменения.

После физического доказательства sleep field/protocol следующий bounded step сможет добавить реальные presets:

```text
5 / 10 / 15 / 30 / 60 min / Never
```

и только после этого policy вроде `RUNNING/WAITING = Never`, `NORMAL = configurable`.

## Конфигурация

Canonical config:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\config.toml
```

Schema v4 сохраняет profile-color model, 30-second DONE fallback, profile-switch overlay OFF и accepted RGB modes.

Production notifier modes:

```text
constant
flowing_water
single_color_breathing
cycle_breathing
off
```

Horse race/native `0x83`, Tetris, Neon и Ambilight остаются исследовательскими режимами Lighting Lab.

## HTML configurator

Bundled offline configurator находится в `configurator/index.html` и открывается как Advanced RGB config. Status Lab передаёт ему текущий path + содержимое `config.toml`; browser не получает прямой write-доступ в `%LOCALAPPDATA%`.

Configurator умеет load, timestamped backup, restore backup, validation и download нового `config.toml`.

## Логирование

Tray/Control Center показывают `Подробный журнал: ВКЛ / ВЫКЛ`.

Даже при OFF минимальные Codex/notification lifecycle events сохраняются, потому что нужны reducer. Journal bounded rotation:

```text
events.jsonl    ~ до 5 MiB
events.jsonl.1
events.jsonl.2
```

Runtime normalizer читает только новые байты, startup rehydrate ограничен recent window.

## Lighting Lab

`Vorotex.K15.LightingLab.exe` остаётся отдельным low-level исследовательским инструментом с palette mask, brightness/speed/direction, user notes и exact restore.

Physical classification:

```text
Flowing Water          controlled
Single-color breathing controlled
Cycle breathing        controlled
Horse race / 0x83      uncontrolled rainbow
Tetris                 работает, но сильно отвлекает
Neon                   uncontrolled multicolor
Ambilight              uncontrolled multicolor
```

## Hardware safety

```text
VID        B6A4 / 36A4
PID        4100 / 4101
UsagePage  FF01
Usage      0001
Report ID  06
Report     41 bytes
lighting write 09
lighting read  89
active slot    82 selector 2
```

Status Lab runtime не пишет firmware, reset, key mappings, macros или unknown power settings. Every touched lighting record основывается на exact rollback snapshot. Нет programmatic A/B switching.

Current physical K15 channel order:

```toml
[device]
wire_color_order = "rgb"
```

## Build / tests

```text
powershell -ExecutionPolicy Bypass -File status-lab/tests/smoke.ps1
powershell -ExecutionPolicy Bypass -File status-lab/tests/rc2-smoke.ps1
dotnet run --project status-lab/tests/StateReducerSmoke.csproj -c Release
dotnet build status-lab/Vorotex.K15.StatusLab.csproj -c Release
dotnet build status-lab/lighting-lab/Vorotex.K15.LightingLab.csproj -c Release
```

RC2 tracking: Issue #25. Merge is gated by CI and owner canary.
