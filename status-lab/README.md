# VOROTEX K15 Status Lab

Windows-компаньон для исследования и канареечного запуска RGB-индикации состояний Codex на VOROTEX K15 Pro.

Status Lab объединяет три слоя:

1. lifecycle hooks Codex;
2. системные уведомления Windows через `UserNotificationListener`;
3. opt-in RGB output на K15 через доказанный W909/W910-family HID protocol.

Локальный журнал:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\events.jsonl
```

## Privacy boundary

В журнал попадают только диагностические метаданные:

- timestamp/source/event;
- Codex session/turn/model/cwd/tool name/permission mode;
- Windows notification id, app identity, creation time;
- SHA-256 fingerprint текста toast, размеры текстовых элементов и coarse hint flags;
- normalized-state и K15 RGB transport events.

Status Lab намеренно **не сохраняет** prompt text, assistant response text, tool input/tool response, transcript contents и открытый текст Windows notification.

## Windows notifications

Status Lab использует:

```text
Windows.UI.Notifications.Management.UserNotificationListener
```

и опрашивает текущий notification store примерно раз в 2 секунды.

События:

- `windows_notification_present`;
- `windows_notification_added`;
- `windows_notification_removed`;
- `notification_access` / `notification_poll_error`.

`removed` означает только, что конкретного toast больше нет в доступном Windows notification store. Это не строгое доказательство «прочитано».

ChatGPT и Codex в текущем OpenAI Windows package имеют общий AppUserModelId/PFN, поэтому attribution строится прежде всего по корреляции с Codex hooks, а не по имени приложения.

Microsoft reference:

- https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.management.usernotificationlistener

## Codex hooks

Status Lab устанавливает пять lifecycle hooks:

```text
UserPromptSubmit
PermissionRequest
PostToolUse
Stop
SessionEnd
```

`PostToolUse` добавлен после физической канарейки: подтверждение permission непосредственно в UI Codex не обязано удалять Windows toast, но успешный `PostToolUse` надёжно показывает, что разрешённый tool уже выполнился и состояние можно вернуть из `WAITING` в `RUNNING`.

Hook input приходит JSON через stdin. `codex-hook-logger.ps1` сохраняет только безопасный subset полей.

Инсталлятор:

- ищет активные Codex homes, включая `%USERPROFILE%\.codex-agentloop`, `%USERPROFILE%\.codex`, другие `%USERPROFILE%\.codex-*` и явный `CODEX_HOME`;
- сохраняет существующие hook groups;
- делает one-time backup `hooks.json.vorotex-k15-status-lab.bak`;
- идемпотентен;
- после записи перечитывает файл и проверяет все пять Status Lab handlers;
- использует допустимый для Codex `SessionEnd timeout = 3s`.

После изменения hooks полностью перезапусти Codex.

Upstream reference: https://github.com/openai/codex

## Normalized state

Status Lab вычисляет:

```text
NORMAL
RUNNING
WAITING
DONE_PENDING_ATTENTION
ERROR
```

Текущие правила:

- `UserPromptSubmit` → `RUNNING`;
- `PermissionRequest` → `WAITING`;
- `PostToolUse` во время `WAITING` → `RUNNING`;
- удаление конкретного correlated permission toast → `RUNNING` как дополнительный путь;
- `Stop` → `DONE_PENDING_ATTENTION`;
- удаление конкретного post-Stop toast → `NORMAL`;
- если completion toast продолжает висеть, `DONE_PENDING_ATTENTION` автоматически истекает через 15 секунд → `NORMAL`;
- `SessionEnd` → `NORMAL`;
- повторные `PermissionRequest` идемпотентны.

Toast keyword heuristic **не имеет права самостоятельно создавать semantic `ERROR`**: физическая канарейка показала false positives. `ERROR` зарезервирован для будущего high-confidence failure source от Codex/AgentLoop.

Используется 400 ms reorder buffer, потому что hook logger и Windows notification poller пишут журнал из независимых процессов. Также разрешена корреляция toast, пришедшего до 2 секунд перед `PermissionRequest`, потому что это реально наблюдалось на Windows.

## K15 HID path

RGB canary открывает только доказанную vendor collection семейства K15/W909/W910:

```text
VID        36A4 / B6A4
PID        4100 / 4101
UsagePage  FF01
Usage      0001
Report ID  06
Report     41 bytes
lighting write 09
lighting read  89
```

Каждая запись подсветки проверяется readback через `0x89`.

Status Lab не пишет key mappings, macros, power settings или firmware.

### Transport faults are not semantic ERROR

Кратковременный `No matching K15 HID response for command 0x82` был физически замечен при работе с устройством. Теперь полный HID read request повторяется несколько раз с новым sequence, а Status Lab при транспортном сбое показывает `RGB: RETRYING` / `RECONNECTED`, а не semantic `ERROR` и не красит клавиатуру красным из-за USB/HID ошибки.

## Profile-aware lighting policy

Принятые owner baselines:

```text
Profile A = red
Profile B = blue
```

`NORMAL` никогда не синтезируется цветом: Status Lab восстанавливает точные байты исходной подсветки текущего профиля.

Для notification states используются только хорошо различимые базовые цвета + белый. Жёлтый/янтарный исключены как недостаточно различимые на физической K15.

```text
NORMAL A                exact Profile A baseline (red)
NORMAL B                exact Profile B baseline (blue)
RUNNING                 white, slow breathing
WAITING                 white, fast breathing
DONE_PENDING_ATTENTION  green breathing
ERROR                    red, fast breathing (reserved high-confidence error)
```

Различие `RUNNING` / `WAITING` сделано скоростью белого breathing, а не близкими оттенками.

## Profile switch overlay

Переключение аппаратного профиля больше не считается ошибкой.

Status Lab опрашивает active onboard slot. При смене:

```text
switch to Profile A
→ red fast breathing for 5 seconds

switch to Profile B
→ blue fast breathing for 5 seconds
```

Profile flash имеет временно более высокий visual priority. Через 5 секунд Status Lab возвращается к текущему notification state:

```text
profile switch flash
        ↓ 5 sec
current normalized state still WAITING
        ↓
white fast breathing
```

Если notification state = `NORMAL`, после 5 секунд восстанавливается точная baseline-подсветка нового профиля.

Для каждого впервые увиденного onboard slot сохраняется собственный lighting snapshot. Это позволяет корректно возвращать A к красному baseline, а B к синему baseline.

## Enabling RGB canary

RGB по умолчанию OFF. Перед включением закрой официальный VOROTEX software и W910 WebDriver.

В tray:

```text
Включить K15 RGB canary
```

При enable Status Lab сохраняет exact lighting header и exact single-color-breathing record текущего onboard profile. При `NORMAL`, manual disable и application exit пытается восстановить соответствующий baseline.

## Expected owner canary

После установки нового build и повторной установки hooks:

```text
Profile B NORMAL
→ exact blue baseline

UserPromptSubmit
→ white slow breathing

PermissionRequest
→ white fast breathing

approve inside Codex
→ PostToolUse
→ white slow breathing

switch B -> A while Codex still running
→ red profile flash for 5 sec
→ white slow breathing resumes

switch A -> B while WAITING
→ blue profile flash for 5 sec
→ white fast breathing resumes

Stop
→ green breathing

completion toast removed OR 15 sec timeout
→ exact baseline of currently active profile
```

Rejected permission may legitimately transition `WAITING → DONE_PENDING_ATTENTION` without an intermediate `RUNNING`.

## Build

Requires Windows + .NET 8 SDK.

```text
dotnet run --project status-lab/Vorotex.K15.StatusLab.csproj
```

Portable publish:

```text
dotnet publish status-lab/Vorotex.K15.StatusLab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Smoke tests:

```powershell
powershell -ExecutionPolicy Bypass -File .\status-lab\tests\smoke.ps1

dotnet run --project status-lab/tests/StateReducerSmoke.csproj -c Release
```

## Canary evidence

- [`docs/owner-canary-2026-08-24.md`](docs/owner-canary-2026-08-24.md) — first Windows notification canary and Codex-home correction.
- [`docs/owner-canary-2-2026-08-24.md`](docs/owner-canary-2-2026-08-24.md) — Codex hook + notification correlation.
- [`docs/owner-canary-3-2026-08-24.md`](docs/owner-canary-3-2026-08-24.md) — dry-run state normalizer accepted.
- [`docs/owner-canary-4-2026-08-24.md`](docs/owner-canary-4-2026-08-24.md) — first physical RGB canary, manual restore proof, automatic-DONE and early-toast corrections.

PR #20 remains intentionally unmerged until the profile-aware RGB canary is physically accepted.
