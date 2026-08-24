using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed class StatusLabApplicationContext : ApplicationContext
{
    private readonly StatusLabConfig _config;
    private readonly WindowsNotificationPoller _notificationPoller = new();
    private readonly JournalStateNormalizer _stateNormalizer;
    private readonly K15RgbCanary _rgbCanary;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _trackingOnIcon;
    private readonly Icon _trackingOffIcon;
    private readonly ToolStripMenuItem _stateStatusItem;
    private readonly ToolStripMenuItem _trackingStatusItem;
    private readonly ToolStripMenuItem _rgbStatusItem;
    private readonly ToolStripMenuItem _rgbCanaryItem;
    private readonly ToolStripMenuItem _notificationStatusItem;
    private readonly ToolStripMenuItem _codexHookItem;
    private readonly ToolStripMenuItem _loggingItem;
    private bool _exiting;

    public StatusLabApplicationContext()
    {
        EventJournal.EnsureExists();
        _config = StatusLabConfig.LoadOrCreate();
        _stateNormalizer = new JournalStateNormalizer(_config.DoneAttentionTimeoutSeconds);
        _rgbCanary = new K15RgbCanary(_config);
        _trackingOnIcon = TrayIconFactory.Create(trackingEnabled: true);
        _trackingOffIcon = TrayIconFactory.Create(trackingEnabled: false);

        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "started",
            version = typeof(StatusLabApplicationContext).Assembly.GetName().Version?.ToString(),
            rgbConfigPath = StatusLabConfig.FilePath,
            configSchema = _config.SchemaVersion,
            wireColorOrder = _config.WireColorOrder.ToString(),
            doneAttentionTimeoutSeconds = _config.DoneAttentionTimeoutSeconds,
            detailedLogging = EventJournal.DetailedLoggingEnabled,
            configWarning = _config.LoadWarning,
            hardwareProfileSelectionPolicy = "observe_only"
        });

        _stateStatusItem = new ToolStripMenuItem("Состояние: NORMAL") { Enabled = false };
        _trackingStatusItem = new ToolStripMenuItem("RGB-индикация: ВЫКЛ") { Enabled = false };
        _notificationStatusItem = new ToolStripMenuItem("Уведомления: запуск...") { Enabled = false };
        _rgbStatusItem = new ToolStripMenuItem("RGB: OFF") { Enabled = false };
        _rgbCanaryItem = new ToolStripMenuItem("Включить RGB-индикацию статусов");
        _rgbCanaryItem.Click += async (_, _) => await ToggleRgbCanaryAsync();
        _codexHookItem = new ToolStripMenuItem("Установить Codex hooks");
        _codexHookItem.Click += async (_, _) => await InstallCodexHooksAsync();
        _loggingItem = new ToolStripMenuItem();
        _loggingItem.Click += (_, _) => ToggleDetailedLogging();
        RefreshLoggingItem();

        _trayIcon = new NotifyIcon
        {
            Icon = _trackingOffIcon,
            Text = "K15 Status Lab · NORMAL · RGB OFF",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notificationPoller.StatusChanged += status => Ui(() => _notificationStatusItem.Text = status);
        _stateNormalizer.StateChanged += (state, transition) =>
        {
            UpdateNormalizedState(state, transition);
            _ = _rgbCanary.ApplyStateAsync(state, transition);
        };
        _rgbCanary.StatusChanged += UpdateRgbStatus;
        _stateNormalizer.Start();
        _ = StartNotificationPollingAsync();
        RefreshTrackingIndicator();

        if (!string.IsNullOrWhiteSpace(_config.LoadWarning))
            ShowBalloon(_config.LoadWarning);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateStatusItem);
        menu.Items.Add(_trackingStatusItem);
        menu.Items.Add(_notificationStatusItem);
        menu.Items.Add(_rgbStatusItem);
        menu.Items.Add("Сбросить состояние в NORMAL", null, (_, _) => _stateNormalizer.Acknowledge());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_rgbCanaryItem);
        menu.Items.Add("Открыть RGB config.toml", null, (_, _) => OpenPath(StatusLabConfig.FilePath));
        menu.Items.Add("Открыть RGB configurator", null, (_, _) => OpenConfigurator());
        menu.Items.Add(BuildEffectLabMenu());
        menu.Items.Add("Открыть K15 Lighting Lab", null, async (_, _) => await OpenLightingLabAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codexHookItem);
        menu.Items.Add(_loggingItem);
        menu.Items.Add("Открыть журнал событий", null, (_, _) => OpenPath(EventJournal.FilePath));
        menu.Items.Add("Открыть папку журнала", null, (_, _) => OpenPath(EventJournal.DirectoryPath));
        menu.Items.Add("Очистить журнал", null, (_, _) => ClearJournal());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        return menu;
    }

    private ToolStripMenuItem BuildEffectLabMenu()
    {
        var lab = new ToolStripMenuItem("RGB Effect Test · quick");
        AddEffectTest(lab, "Constant", K15LightingMode.Constant);
        AddEffectTest(lab, "Flowing Water · one profile color", K15LightingMode.FlowingWater);
        AddEffectTest(lab, "Single-color breathing", K15LightingMode.SingleColorBreathing);
        AddEffectTest(lab, "Cycle breathing · one profile color", K15LightingMode.CycleBreathing);
        lab.DropDownItems.Add(new ToolStripSeparator());
        lab.DropDownItems.Add("Restore exact baseline", null, async (_, _) =>
        {
            try { await _rgbCanary.RestoreCurrentAsync(); }
            catch (Exception ex) { ShowBalloon($"Effect Test restore: {ex.Message}"); }
        });
        return lab;
    }

    private void AddEffectTest(ToolStripMenuItem parent, string title, K15LightingMode mode)
    {
        parent.DropDownItems.Add(title, null, async (_, _) =>
        {
            if (!_rgbCanary.Enabled)
            {
                ShowBalloon("Сначала включи RGB-индикацию статусов. Для полного исследования режимов используй отдельный K15 Lighting Lab.");
                return;
            }
            try { await _rgbCanary.TestEffectAsync(mode); }
            catch (Exception ex) { ShowBalloon($"Effect Test: {ex.Message}"); }
        });
    }

    private void UpdateNormalizedState(K15NormalizedState state, StateTransition? transition)
    {
        var wire = JournalStateNormalizer.ToWireName(state);
        Ui(() =>
        {
            _stateStatusItem.Text = string.IsNullOrWhiteSpace(_stateNormalizer.FocusedSessionId)
                ? $"Состояние: {wire}"
                : $"Состояние: {wire} · Codex {ShortSession(_stateNormalizer.FocusedSessionId)}";
            RefreshTrayTooltip(wire);
        });
    }

    private async Task ToggleRgbCanaryAsync()
    {
        if (_rgbCanary.Enabled)
        {
            _rgbCanaryItem.Enabled = false;
            try
            {
                await _rgbCanary.DisableAsync();
                _rgbCanaryItem.Text = "Включить RGB-индикацию статусов";
            }
            finally
            {
                _rgbCanaryItem.Enabled = true;
                RefreshTrackingIndicator();
            }
            return;
        }

        var result = MessageBox.Show(
            "Включить физическую RGB-индикацию статусов K15?\n\n" +
            "Status Lab только наблюдает физически выбранный Profile A/B и НИКОГДА не переключает его программно. " +
            "Цвет задаёт профиль, состояния и короткие сигналы меняют эффект/палитру. NORMAL восстанавливает exact onboard baseline. " +
            "Закрой W910 WebDriver/VOROTEX перед тестом.\n\nПродолжить?",
            "VOROTEX K15 RGB-индикация", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _rgbCanaryItem.Enabled = false;
        try
        {
            await _rgbCanary.EnableAsync(_stateNormalizer.State);
            _rgbCanaryItem.Text = "Выключить RGB-индикацию статусов";
        }
        catch (Exception ex)
        {
            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "k15_rgb",
                @event = "rgb_enable_failed",
                exception = ex.GetType().FullName,
                hresult = ex.HResult,
                message = ex.Message
            });
            ShowBalloon($"RGB-индикация не запущена: {ex.Message}");
        }
        finally
        {
            _rgbCanaryItem.Enabled = true;
            RefreshTrackingIndicator();
        }
    }

    private void RefreshTrackingIndicator()
    {
        Ui(() =>
        {
            var enabled = _rgbCanary.Enabled;
            _trackingStatusItem.Text = enabled ? "RGB-индикация: ВКЛ" : "RGB-индикация: ВЫКЛ";
            _trayIcon.Icon = enabled ? _trackingOnIcon : _trackingOffIcon;
            _rgbCanaryItem.Text = enabled ? "Выключить RGB-индикацию статусов" : "Включить RGB-индикацию статусов";
            RefreshTrayTooltip(JournalStateNormalizer.ToWireName(_stateNormalizer.State));
        });
    }

    private void RefreshTrayTooltip(string wireState)
    {
        var tracking = _rgbCanary.Enabled ? "RGB ON" : "RGB OFF";
        _trayIcon.Text = $"K15 Status Lab · {wireState} · {tracking}";
    }

    private void ToggleDetailedLogging()
    {
        var enable = !EventJournal.DetailedLoggingEnabled;
        EventJournal.SetDetailedLoggingEnabled(enable);
        RefreshLoggingItem();
        ShowBalloon(enable
            ? "Подробный журнал включён. events.jsonl всё равно ограничен ротацией."
            : "Подробный журнал выключен. Минимальные Codex/notification события остаются для работы отслеживания; журнал автоматически ограничен по размеру.");
    }

    private void RefreshLoggingItem()
    {
        _loggingItem.Text = EventJournal.DetailedLoggingEnabled
            ? "Подробный журнал: ВКЛ"
            : "Подробный журнал: ВЫКЛ";
    }

    private async Task OpenLightingLabAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vorotex.K15.LightingLab.exe");
        if (!File.Exists(path))
        {
            ShowBalloon("Vorotex.K15.LightingLab.exe не найден рядом со Status Lab.");
            return;
        }

        if (_rgbCanary.Enabled)
        {
            try
            {
                await _rgbCanary.DisableAsync("lighting_lab_launch");
                RefreshTrackingIndicator();
                ShowBalloon("RGB-индикация выключена перед Lighting Lab. После исследований включи её снова вручную.");
            }
            catch (Exception ex)
            {
                ShowBalloon($"Не удалось безопасно освободить K15 для Lighting Lab: {ex.Message}");
                return;
            }
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "lighting_lab_launched",
            executable = path
        });
    }

    private void UpdateRgbStatus(string status)
    {
        Ui(() =>
        {
            _rgbStatusItem.Text = status;
            var enabled = _rgbCanary.Enabled;
            _trackingStatusItem.Text = enabled ? "RGB-индикация: ВКЛ" : "RGB-индикация: ВЫКЛ";
            _trayIcon.Icon = enabled ? _trackingOnIcon : _trackingOffIcon;
            _rgbCanaryItem.Text = enabled ? "Выключить RGB-индикацию статусов" : "Включить RGB-индикацию статусов";
            RefreshTrayTooltip(JournalStateNormalizer.ToWireName(_stateNormalizer.State));
        });
    }

    private async Task StartNotificationPollingAsync()
    {
        try
        {
            var started = await _notificationPoller.StartAsync();
            if (!started)
                ShowBalloon("Доступ к уведомлениям не разрешен. Разреши его в Windows и перезапусти Status Lab.");
        }
        catch (Exception ex)
        {
            _notificationStatusItem.Text = $"Уведомления: ошибка 0x{ex.HResult:X8}";
            ShowBalloon($"Не удалось запустить listener: 0x{ex.HResult:X8}");
        }
    }

    private async Task InstallCodexHooksAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "install-codex-hooks.ps1");
        if (!File.Exists(script))
        {
            ShowBalloon("install-codex-hooks.ps1 не найден рядом с приложением.");
            return;
        }

        _codexHookItem.Enabled = false;
        try
        {
            var utf8 = new UTF8Encoding(false);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell process did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0)
            {
                EventJournal.Append(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "status_lab",
                    @event = "codex_hooks_install_failed",
                    exitCode = process.ExitCode,
                    stderr
                });
                ShowBalloon(string.IsNullOrWhiteSpace(stderr)
                    ? $"Установщик hooks завершился с кодом {process.ExitCode}."
                    : $"Не удалось установить hooks. Код {process.ExitCode}. Подробности в журнале.");
                return;
            }

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var count = root.TryGetProperty("count", out var countNode) ? countNode.GetInt32() : 0;
            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "status_lab",
                @event = "codex_hooks_installed",
                count
            });
            ShowBalloon($"Codex hooks установлены в {count} окружение(я). Полностью перезапусти Codex перед тестом.");
        }
        catch (Exception ex)
        {
            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "status_lab",
                @event = "codex_hooks_install_exception",
                exception = ex.GetType().FullName,
                message = ex.Message
            });
            ShowBalloon($"Не удалось установить Codex hooks: {ex.Message}");
        }
        finally { _codexHookItem.Enabled = true; }
    }

    private void OpenConfigurator()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "configurator", "index.html");
        if (!File.Exists(path))
        {
            ShowBalloon("Встроенный RGB configurator не найден рядом со сборкой.");
            return;
        }

        StatusLabConfig.EnsureExists();
        var configText = File.ReadAllText(StatusLabConfig.FilePath, Encoding.UTF8);
        var configData = Convert.ToBase64String(Encoding.UTF8.GetBytes(configText));
        var url = new Uri(path).AbsoluteUri +
                  "?configPath=" + Uri.EscapeDataString(StatusLabConfig.FilePath) +
                  "&configData=" + Uri.EscapeDataString(configData);
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private static void OpenPath(string path)
    {
        if (path == StatusLabConfig.FilePath && !File.Exists(path))
            StatusLabConfig.EnsureExists();
        else if (path == EventJournal.FilePath)
            EventJournal.EnsureExists();
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ClearJournal()
    {
        var result = MessageBox.Show("Очистить локальный диагностический журнал Status Lab и его архивы?",
            "VOROTEX K15 Status Lab", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
            return;
        EventJournal.Clear();
        _stateNormalizer.Acknowledge();
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "journal_cleared"
        });
    }

    private void ShowBalloon(string message)
    {
        _trayIcon.BalloonTipTitle = "VOROTEX K15 Status Lab";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(5000);
    }

    private void Ui(Action action)
    {
        try
        {
            if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
                _trayIcon.ContextMenuStrip.BeginInvoke(action);
            else
                action();
        }
        catch { }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "stopping"
        });
        await _notificationPoller.DisposeAsync();
        await _stateNormalizer.DisposeAsync();
        await _rgbCanary.DisposeAsync();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trackingOnIcon.Dispose();
        _trackingOffIcon.Dispose();
        base.ExitThreadCore();
    }

    private static string ShortSession(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
