# VOROTEX K15 Status Lab

Диагностический Windows-компаньон для проверки двух входных каналов будущего RGB notifier:

1. штатные lifecycle hooks Codex;
2. системные уведомления Windows через `UserNotificationListener`.

На этом этапе Status Lab **не управляет подсветкой K15**. Его задача — собрать воспроизводимую временную шкалу событий до подключения RGB output.

## Что пишет в журнал

Локальный журнал:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\events.jsonl
```

В журнал попадают только метаданные:

- время события;
- источник (`codex_hook` / `windows_notification`);
- тип события;
- Codex `session_id`, `turn_id`, model, cwd, tool name, permission mode;
- Windows notification id, creation time, app display name, AppUserModelId и PackageFamilyName;
- privacy-safe fingerprint текста toast, число/длины текстовых элементов и только вычисленные hint-флаги `permission/completion/error`.

Status Lab намеренно **не сохраняет**:

- prompt text;
- assistant response text;
- tool input;
- notification title/body в открытом виде;
- transcript contents.

## Windows notifications

Status Lab использует:

```text
Windows.UI.Notifications.Management.UserNotificationListener
```

и опрашивает текущий notification store каждые 2 секунды через `GetNotificationsAsync(NotificationKinds.Toast)`.

Polling выбран намеренно. Для unpackaged desktop apps чтение текущих уведомлений работает, но подписка на `NotificationChanged` имеет известные ограничения/ошибки на части Windows 11 builds. Для нашего notifier задержка до ~2 секунд приемлема, а упаковка MSIX пока не требуется.

При первом запуске Windows может запросить разрешение на доступ к уведомлениям.

Текст toast используется только в памяти процесса для SHA-256 fingerprint и грубой классификации по ключевым словам; исходный текст в JSONL не сохраняется.

События:

- `windows_notification_present` — уведомление уже было активно при старте Status Lab;
- `windows_notification_added` — появилось новое;
- `windows_notification_removed` — ранее видимое уведомление исчезло;
- `notification_access` / `notification_poll_error` — диагностика доступа.

Важно: `removed` означает, что notification больше нет в доступном notification store. Это не строгое доказательство «прочитано».

Microsoft reference:

- https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener
- https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.management.usernotificationlistener

## Codex hooks

Status Lab ставит четыре lifecycle hook:

```text
UserPromptSubmit
PermissionRequest
Stop
SessionEnd
```

Они пишут sanitized JSONL через `codex-hook-logger.ps1`.

Установка из tray:

```text
Установить Codex hooks
```

или вручную:

```powershell
powershell -ExecutionPolicy Bypass -File .\status-lab\install-codex-hooks.ps1
```

Инсталлятор:

- автоматически ищет существующие Codex homes, включая `%USERPROFILE%\.codex-agentloop`, `%USERPROFILE%\.codex` и другие `%USERPROFILE%\.codex-*`;
- если `CODEX_HOME` задан в окружении запуска, использует его как приоритетный target;
- сохраняет существующие hook groups в каждом найденном home;
- делает one-time backup `hooks.json.vorotex-k15-status-lab.bak` рядом с каждым изменённым `hooks.json`;
- после записи перечитывает файл и проверяет наличие ровно одного Status Lab handler для каждого события;
- повторный запуск идемпотентен и не создаёт второй набор Status Lab handlers.

После установки **полностью перезапусти Codex**, потому что hooks обнаруживаются при загрузке Codex config/session. Если Codex попросит подтвердить доверие к пользовательским hooks, подтверди их.

Codex upstream:

- lifecycle hooks are a stable feature;
- command hook input is delivered as JSON through stdin;
- `UserPromptSubmit`, `PermissionRequest`, `Stop`, `SessionEnd` are supported hook events.

Reference: https://github.com/openai/codex

## Ожидаемый первый канареечный прогон

1. Запустить Status Lab.
2. Разрешить доступ к Windows notifications.
3. Установить Codex hooks из tray.
4. Перезапустить Codex.
5. Отправить обычную задачу.
6. Добиться запроса permission/user attention, если возможно.
7. Дождаться завершения.
8. Убрать/открыть системное уведомление Codex.
9. Открыть `events.jsonl`.

Ожидаемая последовательность примерно такая:

```text
codex_hook          UserPromptSubmit
codex_hook          PermissionRequest        (если был)
codex_hook          Stop
windows_notification windows_notification_added
windows_notification windows_notification_removed
```

Это не обязательный точный порядок: системное уведомление может появиться немного раньше/позже Stop.

## Запуск из исходников

Требуются Windows и .NET 8 SDK.

```text
dotnet run --project status-lab/Vorotex.K15.StatusLab.csproj
```

## Publish portable folder

```text
dotnet publish status-lab/Vorotex.K15.StatusLab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

В publish-каталог должны попасть:

- `Vorotex.K15.StatusLab.exe`;
- `codex-hook-logger.ps1`;
- `install-codex-hooks.ps1`.

## Smoke tests

```powershell
powershell -ExecutionPolicy Bypass -File .\status-lab\tests\smoke.ps1
```

Smoke test проверяет:

- sanitized hook logging;
- отсутствие prompt text в journal;
- сохранение существующих Codex hooks;
- идемпотентность installer;
- создание one-time backup.

## Canary evidence

- [`docs/owner-canary-2026-08-24.md`](docs/owner-canary-2026-08-24.md) — sanitized findings from the first Windows owner canary, including the `.codex` vs `.codex-agentloop` hook-target correction and notification baseline rules.
- [`docs/owner-canary-2-2026-08-24.md`](docs/owner-canary-2-2026-08-24.md) — second canary: `UserPromptSubmit`, `PermissionRequest`, `Stop` and Windows notification correlation physically observed; dry-run state normalizer is the next gate.

## Dry-run normalized state

After the second owner canary, Status Lab also computes a **dry-run** state without writing K15 lighting:

```text
NORMAL
RUNNING
WAITING
DONE_PENDING_ATTENTION
ERROR
```

The tray shows the current state. Each transition is appended as `source=state_normalizer`, `event=normalized_state_changed`.

Current reducer rules:

- `UserPromptSubmit` → `RUNNING`;
- `PermissionRequest` → `WAITING`;
- the specific correlated OpenAI permission notification disappearing → `RUNNING`;
- `Stop` → `DONE_PENDING_ATTENTION`;
- a specific post-Stop OpenAI notification is tracked as completion attention;
- removing that tracked completion notification → `NORMAL`;
- a post-Stop notification with a coarse error hint may temporarily produce `ERROR`;
- `SessionEnd` → `NORMAL`;
- repeated `PermissionRequest` events are idempotent.

A 400 ms reorder buffer is used because hook and Windows-notification writers are independent processes and their JSONL append order can differ slightly from event timestamps.

The normalizer also binds an OpenAI toast that appears up to 2 seconds **before** a `PermissionRequest` hook. This was observed physically because the notification writer can win the race by ~100 ms. A `DONE_PENDING_ATTENTION` / `ERROR` state is bounded to 15 seconds; if the completion toast remains in Windows Notification Center, Status Lab restores `NORMAL` automatically instead of holding the K15 lighting indefinitely.

The tray action **Сбросить состояние в NORMAL** provides manual acknowledgement during the canary.

RGB writes are still disabled in this stage.

## Opt-in K15 RGB canary

After the third owner canary, the source and dry-run normalization layers are accepted for a guarded physical lighting test.

RGB remains **OFF by default**. Enable it manually from the tray:

```text
Включить K15 RGB canary
```

The canary:

- opens only the vendor HID collection for the proven K15/W909/W910 family (`36A4/B6A4 : 4100/4101`, usage page `FF01`, usage `0001`, 41-byte feature report);
- captures the current onboard profile slot;
- captures the exact 25-byte lighting header and exact 25-byte single-color-breathing record before the first write;
- changes only the lighting header and the single-color-breathing record;
- verifies every write with HID readback;
- restores the exact captured bytes on `NORMAL`, manual disable, and application exit;
- refuses writes if the active onboard profile changes while the snapshot is held;
- never writes key mappings, macros, power settings, firmware, or other profile banks.

Close the official VOROTEX software and W910 WebDriver before enabling the RGB canary. Do not switch the K15 hardware profile while the canary is active.

Current canary colors use hardware single-color breathing:

```text
RUNNING                violet
WAITING                amber
DONE_PENDING_ATTENTION green
ERROR                  red
NORMAL                 restore exact original lighting bytes
```

The RGB implementation uses the same report framing proven by the open W910 protocol research:

```text
report id       = 0x06
report size     = 41
lighting write  = 0x09
lighting read   = 0x89
detail record   = 25 bytes
wire color order = G,R,B
```

Every RGB action is logged as `source=k15_rgb`.
## Следующий gate

Source capture and dry-run normalization are accepted. The next owner canary is the first **physical RGB automation** test.

Expected visual sequence:

```text
NORMAL
  -> original lighting restored

UserPromptSubmit
  -> violet breathing

PermissionRequest
  -> amber breathing

permission notification resolved
  -> violet breathing

Stop
  -> green breathing

tracked completion notification removed
  -> exact original lighting restored
```

A rejected permission may legitimately go from amber directly to green if Codex stops the turn without resuming work.

Record after the canary:

```text
RGB_ENABLE = PASS/FAIL
RGB_RUNNING_BLUE = PASS/FAIL
RGB_WAITING_AMBER = PASS/FAIL
RGB_DONE_GREEN = PASS/FAIL
RGB_RESTORE_EXACT = PASS/FAIL
RGB_READBACK_VERIFY = PASS/FAIL
PROFILE_SWITCH_SAFETY = NOT_TESTED/REFUSED_AS_EXPECTED
```

`SessionEnd` remains configured but is not required for this gate.
