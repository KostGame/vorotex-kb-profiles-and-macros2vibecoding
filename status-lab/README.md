# VOROTEX K15 Status Lab

Windows-компаньон для RGB-индикации состояний Codex на VOROTEX K15 Pro.

Status Lab объединяет:

1. lifecycle hooks Codex;
2. Windows notifications через `UserNotificationListener`;
3. normalized state machine;
4. opt-in RGB output через доказанный W909/W910-family HID protocol.

Локальный журнал:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\events.jsonl
```

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
- если completion toast остаётся, DONE автоматически истекает через 15 секунд -> `NORMAL`;
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

Lighting record использует физически подтверждённый порядок `G,R,B` на проводе. Каждая lighting write проверяется readback через `0x89`.

Status Lab не пишет key mappings, macros, power settings или firmware.

### Transport faults are not semantic ERROR

Кратковременные `0x82` timeouts/transition values рассматриваются как HID transport state. Active-slot read повторяется до стабилизации, reconnect показывается как `RGB: RETRYING` / `RECONNECTED`, а не semantic ERROR.

Отключение RGB не должно ронять приложение: restore failure журналируется, HID handle всё равно освобождается.

## Profile-aware baseline

Принятые owner baselines:

```text
Profile A = red Constant
Profile B = blue Constant
```

`NORMAL` не синтезируется цветом: Status Lab восстанавливает exact baseline bytes текущего профиля.

После canary, где Profile B остался в breathing, добавлена self-heal защита. Если впервые увиденный A/B profile содержит известный notifier-mode `0x84` (Single-color breathing) или `0x86` (Tetris), Status Lab возвращает только lighting header в `Constant 0x81`. Constant-mode data никогда не изменяется Status Lab, поэтому исходный red/blue baseline сохраняется.

## Notification lighting policy

Жёлтый/янтарный исключены как недостаточно различимые на физической K15.

```text
NORMAL A                exact red Constant baseline
NORMAL B                exact blue Constant baseline
RUNNING                 built-in Tetris blocks effect
WAITING                 white fast breathing
DONE_PENDING_ATTENTION  green breathing
ERROR                    red fast breathing, reserved high-confidence error
```

White slow vs white fast был физически отвергнут как слишком похожий, поэтому RUNNING теперь использует встроенный `Tetris blocks` (`0x86`). Status Lab переключает только mode header и не переписывает onboard Tetris detail record.

## Profile switch overlay

При ручном переключении:

```text
switch to A -> red fast breathing for 5 seconds
switch to B -> blue fast breathing for 5 seconds
```

Через 5 секунд возвращается актуальное notification state. Если state = NORMAL, возвращается exact baseline нового профиля.

Важная коррекция после физической канарейки: если пользователь переключился B -> A до завершения пятисекундного B-overlay, старый build оставлял breathing header в Profile B. Теперь Status Lab под gate:

1. видит новый active slot;
2. временно выбирает предыдущий slot через доказанный `0x02 / selector 2`;
3. восстанавливает exact cached baseline предыдущего профиля;
4. сразу возвращает выбранный пользователем новый slot;
5. запускает 5-second overlay нового профиля.

Так notifier не должен оставлять скрытый профиль в мигающем состоянии даже при быстром A/B switching.

## Avoiding visible wrong-color transients

Single-color breathing хранит mode header и detail palette отдельно. Старый порядок сначала включал breathing header, а потом писал новую palette. На несколько миллисекунд могла стать видна старая palette, что воспринималось как красный/зелёный неправильный flash.

Теперь:

```text
apply breathing:
  detail first
  header second

restore NORMAL:
  Constant baseline header first
  hidden breathing detail second
```

Это также убирает наблюдавшийся красный flash непосредственно перед NORMAL.

## RGB canary

RGB по умолчанию OFF. Перед включением закрой официальный VOROTEX software и W910 WebDriver.

Tray:

```text
Включить K15 RGB canary
```

Profile A/B можно переключать во время canary.

## Next owner gate

### A. Profile switching / cleanup

```text
A red constant
A -> B
blue profile flash 5s
-> blue constant

rapid B -> A before B flash finishes
red profile flash
-> red constant

turn RGB OFF
switch A/B manually
both profiles remain constant
no .NET exception
```

### B. Codex states

```text
UserPromptSubmit -> Tetris
PermissionRequest -> white fast breathing
approve -> PostToolUse -> Tetris
Stop -> green breathing
completion removed OR 15s -> current profile Constant baseline
```

Switching A/B during a notification state must show profile color for 5 seconds, then resume that state.

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

PR #20 remains intentionally unmerged until the corrected profile-aware RGB canary is physically accepted.
