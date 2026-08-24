# VOROTEX K15 Status Lab

Windows tray helper для RGB-индикации состояний Codex на VOROTEX K15 Pro.

Текущая продуктовая модель:

```text
PROFILE A = RED
PROFILE B = BLUE

цвет   -> какой hardware profile активен
эффект -> что сейчас делает агент
```

Состояния не владеют цветом. `NORMAL` не является notification-effect и всегда восстанавливает точный onboard baseline, считанный с клавиатуры до первой записи Status Lab.

## Источники состояния

Primary semantic source: Codex lifecycle hooks.

```text
UserPromptSubmit  -> RUNNING
PermissionRequest -> WAITING
PostToolUse       -> RUNNING после approval
Stop              -> DONE_PENDING_ATTENTION
SessionEnd        -> NORMAL
```

Windows `UserNotificationListener` остаётся supplemental attention channel. Toast keyword heuristics не имеют права самостоятельно создавать semantic `ERROR`.

HID transport failure и semantic agent error являются разными доменами. `0x82` timeout/transition отображается как transport `RETRYING/RECONNECTED`, а не как semantic `ERROR`.

## Конфигурация

Canonical user config:

```text
%LOCALAPPDATA%\VOROTEX\K15 Status Lab\config.toml
```

Формат TOML выбран специально для комментариев и подсказок рядом с параметрами. Первый запуск создаёт annotated config. Пример также поставляется в artifact как `status-lab-config.example.toml`.

Главное структурное правило:

```toml
[profiles.A]
color = "#FF0000"

[profiles.B]
color = "#0000FF"

[states.running]
effect = "mono_water"
brightness = 4
speed = 3
```

В `[states.*]` нет `color`. Renderer всегда использует цвет активного профиля.

Если `config.toml` невалиден:

- пользовательский файл сохраняется без изменений;
- Status Lab запускается с безопасными defaults для текущей сессии;
- tray показывает понятную ошибку с путём/строкой;
- приложение не перезаписывает ошибочный файл дефолтами.

Изменения TOML применяются после перезапуска Status Lab в этой итерации.

Старый `status-lab-config.example.json` остаётся только как исторический артефакт прототипа и больше не публикуется в runtime artifact.

## Встроенный HTML configurator

В portable artifact находится:

```text
configurator\index.html
```

Tray:

```text
Открыть RGB configurator
```

Configurator полностью локальный:

- без сервера;
- без network calls;
- без telemetry;
- Profile A/B color picker + HEX;
- dropdown всех известных K15 effect modes;
- brightness/speed/direction/duration controls;
- activation checkbox;
- profile-switch settings;
- wire color order;
- предупреждения для потенциально многоцветных/раздражающих modes;
- Load `config.toml` через browser File API;
- live validation;
- generated annotated TOML preview;
- Download `config.toml`.

Browser сам не записывает `%LOCALAPPDATA%`: первая версия скачивает готовый `config.toml`, который пользователь заменяет вручную.

## Default RGB policy

До физической Effect Lab классификации defaults являются кандидатами, а не финальным решением:

```text
NORMAL   -> exact device baseline
RUNNING  -> Mono Water, profile color
WAITING  -> Single-color breathing, profile color
DONE     -> Single-color breathing, profile color, 10 s
ERROR    -> Single-color breathing, profile color, reserved high-confidence source

profile switch -> Mono Water, NEW profile color, 2 s, then resume semantic state
activation     -> OFF
```

Tetris больше не является default. Он сохранён как явно выбираемый experimental mode для исследований.

## Effect Lab

Tray submenu:

```text
RGB Effect Test
  Constant (control)
  Mono Water
  Single-color breathing
  Flowing Water (single color)
  Restore exact baseline
```

Effect Lab доступен только когда RGB canary уже включён. Каждый тест использует единственный configured color текущего profile и автоматически восстанавливает exact baseline после `effect_lab.test_duration_seconds`.

Первый physical gate:

```text
EFFECT-LAB-001

Profile A / RED:
Constant -> Restore
Mono Water -> Restore
Single-color breathing -> Restore
Flowing Water single RED -> Restore

Profile B / BLUE:
повторить те же тесты single BLUE

RGB OFF
manual A -> B -> A
```

Owner классифицирует каждый эффект как `GOOD / ACCEPTABLE / ANNOYING / MULTICOLOR`. Только после этого выбираются постоянные defaults.

## Exact rollback

`DeviceBaselineSnapshot` является rollback authority.

Перед первой RGB-записью для профиля Status Lab читает:

- точный lighting header;
- baseline mode record, если mode известен;
- все mode records, которые notifier/Effect Lab потенциально может затронуть.

Никакой configured normal не записывается до snapshot.

При A -> B / B -> A:

1. exact restore профиля, который пользователь покидает;
2. возврат на выбранный пользователем новый slot;
3. exact snapshot нового profile до первой записи, если он ещё не встречался;
4. profile-switch overlay в цвете нового profile;
5. resume текущего semantic state.

При RGB OFF / application exit:

- сначала требуется стабильный `0x82` active slot;
- если slot не стабилизируется, Status Lab не угадывает профиль и не делает потенциально неверную запись;
- если slot известен, best-effort восстанавливаются все touched profile snapshots;
- затем возвращается профиль, выбранный пользователем;
- HID handle освобождается независимо от restore failure;
- WinForms process не должен падать из-за transport exception.

## K15 HID path

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
slot select    02 selector 2
```

Каждая lighting write проверяется readback через `0x89`.

Текущий физически принятый default channel order:

```toml
[device]
wire_color_order = "rgb"
```

`grb` оставлен как explicit compatibility option.

Status Lab не пишет key mappings, macros, power settings или firmware.

## Tray

```text
Состояние: ...
Уведомления: ...
RGB: ...
Сбросить состояние в NORMAL

Включить/Выключить K15 RGB canary
Открыть RGB config.toml
Открыть RGB configurator
RGB Effect Test >
Установить Codex hooks
Открыть журнал событий
Открыть папку журнала
Очистить журнал
Выход
```

## Build / tests

```text
dotnet run --project status-lab/Vorotex.K15.StatusLab.csproj
powershell -ExecutionPolicy Bypass -File status-lab/tests/smoke.ps1
dotnet run --project status-lab/tests/StateReducerSmoke.csproj -c Release
dotnet build status-lab/Vorotex.K15.StatusLab.csproj -c Release
dotnet publish status-lab/Vorotex.K15.StatusLab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

PR/CI должны пройти до физического `EFFECT-LAB-001`. Финальные effect defaults принимаются только по owner canary на реальной K15.
