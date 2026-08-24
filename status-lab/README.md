# VOROTEX K15 Status Lab

Windows tray helper для RGB-индикации состояний Codex на VOROTEX K15 Pro.

## Продуктовая модель

```text
PROFILE A = RED
PROFILE B = BLUE

цвет   -> какой hardware profile активен
эффект -> что сейчас делает агент
```

`NORMAL` не является notification-effect. Он восстанавливает точный onboard baseline, считанный до первой записи Status Lab. Status Lab наблюдает физический A/B и никогда не переключает hardware profile программно.

Текущие physically accepted defaults:

```text
RGB-индикация ON -> Flowing Water RED <-> BLUE, короткий сигнал
RUNNING           -> Flowing Water, цвет активного профиля
WAITING           -> Single-color breathing, speed 7, цвет профиля
STOP event        -> Cycle breathing RED <-> BLUE, короткий overlay
DONE              -> Single-color breathing, speed 5, цвет профиля
NORMAL            -> exact device baseline
ERROR             -> reserved / disabled
```

У физической K15 также обнаружена собственная тройная flash-анимация при переключении A/B. Она не создаётся Status Lab. Поэтому default `profile_switch.duration_seconds = 4`, чтобы наш Flowing Water оставался видимым после штатных flashes клавиатуры.

## Источники состояния

Primary semantic source: Codex lifecycle hooks.

```text
UserPromptSubmit  -> RUNNING
PermissionRequest -> WAITING
PostToolUse       -> RUNNING после approval
Stop              -> DONE_PENDING_ATTENTION
SessionEnd        -> завершает только свою session
```

Reducer session-aware: хранит `sessionId`, `turnId`, `cwd`, не даёт `.codex-agentloop\memories` / `.codex\memories` перехватывать foreground и при завершении focused session выбирает последнюю живую task-session. При запуске Status Lab recent hooks переигрываются для восстановления текущего состояния.

Windows `UserNotificationListener` остаётся supplemental attention channel. Toast keyword heuristics не имеют права самостоятельно создавать semantic `ERROR`.

## DONE и автоматический возврат в NORMAL

Visual duration эффекта и semantic DONE timeout разделены.

```toml
[behavior]
done_attention_timeout_seconds = 15
```

DONE завершается при удалении сопоставленного completion-notification либо по этому safety timeout. `0` отключает fallback.

В owner trace был обнаружен реальный race: completion toast появился примерно за 0.1 секунды **до** `Stop`. Reducer теперь умеет привязывать recent OpenAI notification и к WAITING, и к DONE, поэтому последующее удаление такого toast возвращает состояние в NORMAL.

Существующий schema-v3 `config.toml` без `[behavior]` автоматически получает safe default 15 секунд в памяти и не переписывается.

## Конфигурация

Canonical config:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\config.toml
```

TOML сохраняет комментарии. Невалидный пользовательский файл не перезаписывается, для текущего запуска используются безопасные defaults с предупреждением.

Palette sources:

```text
profile      -> цвет физически активного Profile A/B
profile_pair -> два canonical цвета A + B
```

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

Bundled offline configurator находится в `configurator/index.html` и открывается из tray.

При открытии через Status Lab ему передаются путь и содержимое текущего `config.toml`, поэтому редактор сразу начинает с активной конфигурации. Браузер намеренно не получает прямой write-доступ в `%LOCALAPPDATA%`.

Configurator умеет:

- показывать путь текущего config;
- загрузить другой `config.toml`;
- скачать timestamped backup текущего config;
- загрузить backup обратно в редактор;
- редактировать profile colors, states, palettes, brightness/speed/direction/duration;
- менять `DONE fallback timeout`;
- генерировать и скачать новый `config.toml` для ручной замены.

## Логирование и лимиты

Tray содержит:

```text
Подробный журнал: ВКЛ / ВЫКЛ
```

Выключение подробного журнала не отключает минимальный transport, нужный самому reducer: Codex lifecycle hooks и OpenAI notification events продолжают попадать в operational journal. Опциональные Status Lab diagnostics отбрасываются.

`events.jsonl` ограничен ротацией:

```text
events.jsonl    ~ до 5 MiB
events.jsonl.1  предыдущий сегмент
events.jsonl.2  ещё один сегмент
```

Итого устойчивый footprint около 15 MiB плюс небольшой overshoot одной записи. Файл не растёт бесконечно.

Во время работы normalizer читает только новые байты с последней позиции, а не перечитывает весь журнал каждые 200 мс. Startup rehydrate ограничен recent window 30 минут / максимум 5000 строк и использует новый архив + текущий journal.

## RGB Effect Test и Lighting Lab

Quick tray test:

```text
Constant
Flowing Water
Single-color breathing
Cycle breathing
Restore exact baseline
```

Отдельный `Vorotex.K15.LightingLab.exe` поставляется в том же portable artifact. Он предназначен для low-level исследований native modes, brightness/speed/direction, 7 palette slots + selection mask, user notes и exact restore. Лог лаборатории: `lighting-lab.jsonl`.

Physical classification:

```text
Flowing Water          controlled, 1/2 selected colors работают
Single-color breathing controlled
Cycle breathing        controlled, 1/2 selected colors работают
Horse race / 0x83      uncontrolled rainbow, research-only
Tetris                 работает, но сильно отвлекает, future/research
Neon                   uncontrolled multicolor
Ambilight              uncontrolled multicolor, возможный future idle/charging
```

## Hardware safety

K15 lighting path:

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

Status Lab и Lighting Lab не пишут firmware, reset, key mappings, macros или power settings. Every touched lighting record основывается на exact rollback snapshot. Если active slot transient/unknown, программа не угадывает профиль и не пишет в неизвестный bank.

Current physical K15 channel order:

```toml
[device]
wire_color_order = "rgb"
```

## Tray

Основные пункты:

```text
Состояние: ... · Codex <session>
RGB-индикация: ВКЛ/ВЫКЛ
Уведомления: ...
RGB: ...
Сбросить состояние в NORMAL

Включить/Выключить RGB-индикацию статусов
Открыть RGB config.toml
Открыть RGB configurator
RGB Effect Test · quick >
Открыть K15 Lighting Lab
Установить Codex hooks
Подробный журнал: ВКЛ/ВЫКЛ
Открыть журнал событий
Открыть папку журнала
Очистить журнал
Выход
```

Tray icon имеет разные OFF/ON варианты, поэтому RGB-индикацию видно без открытия меню.

## Build / tests

```text
powershell -ExecutionPolicy Bypass -File status-lab/tests/smoke.ps1
dotnet run --project status-lab/tests/StateReducerSmoke.csproj -c Release
dotnet build status-lab/Vorotex.K15.StatusLab.csproj -c Release
dotnet build status-lab/lighting-lab/Vorotex.K15.LightingLab.csproj -c Release
```

PR #22 остаётся unmerged до финального owner canary на реальной K15.
