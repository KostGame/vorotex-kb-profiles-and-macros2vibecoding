# VOROTEX K15 Status Lab

Windows-компаньон для RGB-индикации состояний Codex на VOROTEX K15 Pro.

Status Lab объединяет:

1. lifecycle hooks Codex;
2. Windows notifications через `UserNotificationListener`;
3. normalized state machine;
4. opt-in RGB output через доказанный W909/W910-family HID protocol;
5. пользовательский RGB policy config без перекомпиляции приложения.

Локальный журнал:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\events.jsonl
```

Локальный RGB config:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\config.json
```

В tray есть пункт `Открыть RGB config`. Изменения config применяются после полного перезапуска Status Lab.

## Privacy boundary

Журнал хранит только диагностические метаданные: timestamps, source/event, безопасный subset Codex hook metadata, Windows notification identity/fingerprint, normalized-state и K15 transport events.

Status Lab намеренно не сохраняет prompt text, assistant response text, tool input/response, transcript contents и открытый текст Windows notifications.

## Codex hooks

Устанавливаются пять hooks:

```text
UserPromptSubmit
PermissionRequest
PostToolUse
Stop
SessionEnd
```

Ключевая коррекция после физической канарейки:

```text
PermissionRequest -> WAITING
successful PostToolUse -> RUNNING
```

Подтверждение permission внутри Codex не обязано удалять Windows toast, поэтому `PostToolUse` является более сильным сигналом продолжения работы. Удаление correlated permission toast остаётся дополнительным путём `WAITING -> RUNNING`.

Инсталлятор ищет активные Codex homes (`.codex-agentloop`, `.codex`, `.codex-*`, explicit `CODEX_HOME`), сохраняет существующие hook groups, делает one-time backup и идемпотентно проверяет все пять handlers после записи.

После изменения hooks полностью перезапусти Codex.

## Normalized state

```text
NORMAL
RUNNING
WAITING
DONE_PENDING_ATTENTION
ERROR
```

Текущие правила:

- `UserPromptSubmit` -> `RUNNING`;
- `PermissionRequest` -> `WAITING`;
- `PostToolUse` во время `WAITING` -> `RUNNING`;
- correlated permission toast removed -> `RUNNING` как дополнительный путь;
- `Stop` -> `DONE_PENDING_ATTENTION`;
- post-Stop toast removed -> `NORMAL`;
- если completion toast остаётся, semantic DONE сейчас также ограничен защитным 15-second timeout -> `NORMAL`;
- `SessionEnd` -> `NORMAL`.

Toast keyword heuristic не имеет права самостоятельно создавать semantic `ERROR`. `ERROR` зарезервирован для будущего high-confidence failure source от Codex/AgentLoop.

## K15 HID path

```text
VID        36A4 / B6A4
PID        4100 / 4101
UsagePage  FF01
Usage      0001
Report ID  06
Report     41 bytes
lighting write 09
lighting read  89
active-slot read  82 selector 2
slot select       02 selector 2
```

Каждая lighting write проверяется readback через `0x89`.

Status Lab не пишет key mappings, macros, power settings или firmware.

### Physical channel calibration

Open W910 research предполагает `G,R,B`, но физическая VOROTEX K15 canary показала обратное для нашего экземпляра: semantic red при старой GRB-записи физически становился зелёным.

Поэтому default:

```json
"wireColorOrder": "rgb"
```

Это даёт ожидаемые физические primary colors на тестируемой K15. Для совместимости/повторной калибровки config допускает `"grb"`.

### Transport faults are not semantic ERROR

Кратковременные `0x82` timeouts/transition values рассматриваются как HID transport state. Active-slot read повторяется до стабилизации, reconnect показывается как `RGB: RETRYING` / `RECONNECTED`, а не semantic ERROR.

Отключение RGB не должно ронять приложение: restore failure журналируется, HID handle всё равно освобождается.

## Editable RGB config

Status Lab создаёт `config.json` автоматически при первом запуске. В artifact также публикуется `status-lab-config.example.json`.

Поддерживаемые `mode`:

```text
constant
flowingWater
monoWater
singleColorBreathing
cycleBreathing
tetrisBlocks
neon
ambilight
off
```

Для каждого effect можно задавать:

```json
{
  "enabled": true,
  "mode": "singleColorBreathing",
  "brightness": 6,
  "speed": 7,
  "direction": 0,
  "durationSeconds": 5,
  "colors": ["red"]
}
```

Диапазоны:

```text
brightness       1..6
speed            1..7
direction        0..1
durationSeconds  0..3600
colors           максимум 7
```

`durationSeconds = 0` у state effect означает: показывать его до следующего normalized-state transition. Для временных overlays (`activationSignal`, profile `switchSignal`) default duration > 0.

Цвета можно задавать именами или `#RRGGBB`:

```text
red green blue white black cyan magenta purple yellow
#RRGGBB
```

## Accepted profile baseline defaults

Owner baseline зафиксирован прямо в editable config:

```text
Profile A normal = red Constant
Profile B normal = blue Constant
```

При первом обнаружении профиля во время RGB session Status Lab применяет его configured `normal`, а затем сохраняет точный snapshot header + всех mode records, которые может затронуть notifier. Это одновременно чинит старый persisted breathing residue и даёт exact in-process restore после временных эффектов.

Default config можно менять. Например, если позже базовая подсветка Profile B должна быть не синей, достаточно изменить `profiles.b.normal` и перезапустить Status Lab.

## Default notification policy

Жёлтый/янтарный исключены как недостаточно различимые на физической K15.

Default:

```text
NORMAL A                configured red Constant
NORMAL B                configured blue Constant
RUNNING                 Tetris blocks
WAITING                 white fast breathing
DONE_PENDING_ATTENTION  green breathing, 15s max visual duration
ERROR                    red fast breathing, reserved high-confidence error
```

Все эти параметры теперь являются config, а не hardcoded policy.

## Activation signal

При каждом ручном включении RGB canary сначала один раз показывается `activationSignal`, чтобы периферическим зрением было понятно, что клавиатура перешла в режим индикации уведомлений.

Default согласно owner request:

```text
mode        Flowing Water
speed       7 (max)
brightness  4
colors      red + blue
duration    3 seconds
```

После activation overlay автоматически возвращается текущее notification state. Если состояние `NORMAL`, возвращается normal текущего профиля.

## Profile switch overlay

Profile switch signal полностью задаётся в config отдельно для A и B.

Default:

```text
switch to A -> red fast breathing 5s
switch to B -> blue fast breathing 5s
```

Profile switch overlay имеет временный приоритет над notification state. Через `durationSeconds` возвращается актуальное состояние. Пример:

```text
WAITING
-> switch A -> B
-> blue profile signal 5s
-> WAITING effect again
```

Если state = NORMAL, после overlay возвращается configured normal нового профиля.

При быстром A/B switching Status Lab временно выбирает предыдущий slot через доказанный `0x02 / selector 2`, восстанавливает cached baseline предыдущего профиля и сразу возвращается на выбранный пользователем slot. Это не должно оставлять скрытый профиль в notifier mode.

## Effect record writes

Для любого config-driven effect Status Lab:

1. пишет detail record конкретного mode по адресу `(mode & 0x3F) * 25`;
2. проверяет exact readback;
3. только после этого переключает lighting header на выбранный mode;
4. снова проверяет readback.

Так новый palette/speed уже записан до визуального включения эффекта, что уменьшает wrong-color transients.

При restore сначала восстанавливается baseline mode record, затем baseline header, затем скрытые records остальных затронутых режимов.

## RGB canary

RGB по умолчанию OFF. Перед включением закрой официальный VOROTEX software и W910 WebDriver.

Tray:

```text
Включить K15 RGB canary
Открыть RGB config
```

Profile A/B можно переключать во время canary.

## Next owner gate

После изменения policy/config layer нужен один короткий physical canary:

```text
enable RGB
-> Flowing Water red+blue, speed 7, brightness 4
-> current profile baseline / current state

UserPromptSubmit
-> Tetris (default RUNNING)

PermissionRequest
-> white fast breathing

approve
-> PostToolUse -> Tetris

Stop
-> physical green breathing

completion removed OR visual duration expires
-> configured current-profile normal
```

Отдельно:

```text
A -> B during state
-> blue profile overlay 5s
-> resume state

B -> A
-> physical RED profile overlay, not green
-> resume state / red Constant baseline
```

## Build

```text
dotnet run --project status-lab/Vorotex.K15.StatusLab.csproj

dotnet publish status-lab/Vorotex.K15.StatusLab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Smoke tests:

```powershell
powershell -ExecutionPolicy Bypass -File .\status-lab\tests\smoke.ps1

dotnet run --project status-lab/tests/StateReducerSmoke.csproj -c Release
```

## Canary evidence

- [`docs/owner-canary-2026-08-24.md`](docs/owner-canary-2026-08-24.md)
- [`docs/owner-canary-2-2026-08-24.md`](docs/owner-canary-2-2026-08-24.md)
- [`docs/owner-canary-3-2026-08-24.md`](docs/owner-canary-3-2026-08-24.md)
- [`docs/owner-canary-4-2026-08-24.md`](docs/owner-canary-4-2026-08-24.md)
- [`docs/owner-canary-5-2026-08-24.md`](docs/owner-canary-5-2026-08-24.md)

PR #20 remains intentionally unmerged until the config-driven RGB canary is physically accepted.
