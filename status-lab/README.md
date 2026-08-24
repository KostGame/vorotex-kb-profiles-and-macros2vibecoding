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

## Следующий gate

После физического канареечного прогона на Windows:

```text
CODEX_HOOK_EVENTS = PASS/FAIL
WINDOWS_NOTIFICATION_POLL = PASS/FAIL
CODEX_NOTIFICATION_IDENTITY = <AppUserModelId / PFN>
CHATGPT_NOTIFICATION_IDENTITY = <AppUserModelId / PFN>
```

Только после этого Status Lab превращается в K15 notifier и начинает выдавать нормализованные состояния `RUNNING / WAITING / DONE / ERROR` в уже доказанный WebHID lighting path.
