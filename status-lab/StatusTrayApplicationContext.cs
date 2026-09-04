using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed class StatusTrayApplicationContext : ApplicationContext
{
    private readonly StatusLabConfig _config;
    private readonly WindowsNotificationPoller _notificationPoller = new();
    private readonly JournalStateNormalizer _stateNormalizer;
    private readonly K15DeviceManager _deviceManager;
    private readonly K15RgbCanary _rgbCanary;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _trackingOnIcon;
    private readonly Icon _trackingOffIcon;
    private readonly ToolStripMenuItem _stateStatusItem;
    private readonly ToolStripMenuItem _trackingStatusItem;
    private readonly ToolStripMenuItem _rgbStatusItem;
    private readonly ToolStripMenuItem _deviceStatusItem;
    private readonly ToolStripMenuItem _deviceMenu;
    private readonly ToolStripMenuItem _rgbItem;
    private readonly ToolStripMenuItem _notificationStatusItem;
    private readonly ToolStripMenuItem _codexHookItem;
    private readonly ToolStripMenuItem _loggingItem;
    private readonly CancellationTokenSource _ipcCancellation = new();

    private string _notificationStatusText = "Уведомления: запуск...";
    private string _rgbStatusText = "RGB: OFF";
    private string _lastTransitionReason = "state_rehydrated";
    private DateTimeOffset _stateEnteredUtc = DateTimeOffset.UtcNow;
    private bool _exiting;

    public StatusTrayApplicationContext()
    {
        EventJournal.EnsureExists();
        _config = StatusLabConfig.LoadOrCreate();
        _stateNormalizer = new JournalStateNormalizer(_config.StaleAttentionTimeoutSeconds);
        _deviceManager = new K15DeviceManager(Path.Combine(EventJournal.DirectoryPath, "preferred-device.json"));
        _rgbCanary = new K15RgbCanary(_config, _deviceManager);
        _trackingOnIcon = TrayIconFactory.Create(trackingEnabled: true);
        _trackingOffIcon = TrayIconFactory.Create(trackingEnabled: false);

        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_tray",
            @event = "started",
            version = typeof(StatusTrayApplicationContext).Assembly.GetName().Version?.ToString(),
            productSurface = "status_tray_rc2_split",
            configSchema = _config.SchemaVersion,
            staleAttentionTimeoutSeconds = _config.StaleAttentionTimeoutSeconds,
            hardwareProfileSelectionPolicy = "observe_only",
            unknownPowerWrites = false,
            ipc = StatusTrayIpc.PipeName
        });

        _stateStatusItem = new ToolStripMenuItem("Состояние: NORMAL") { Enabled = false };
        _trackingStatusItem = new ToolStripMenuItem("RGB-индикация: ВЫКЛ") { Enabled = false };
        _notificationStatusItem = new ToolStripMenuItem(_notificationStatusText) { Enabled = false };
        _rgbStatusItem = new ToolStripMenuItem(_rgbStatusText) { Enabled = false };
        _deviceStatusItem = new ToolStripMenuItem("Устройство: не подключено") { Enabled = false };
        _deviceMenu = new ToolStripMenuItem("K15 устройство");
        _rgbItem = new ToolStripMenuItem("Включить RGB-индикацию статусов");
        _rgbItem.Click += async (_, _) => await ToggleRgbAsync();
        _codexHookItem = new ToolStripMenuItem("Установить / обновить Codex hooks");
        _codexHookItem.Click += async (_, _) => await InstallCodexHooksAsync();
        _loggingItem = new ToolStripMenuItem();
        _loggingItem.Click += (_, _) => ToggleDetailedLogging();
        RefreshLoggingItem();

        _trayIcon = new NotifyIcon
        {
            Icon = _trackingOffIcon,
            Text = "K15 Status Tray · NORMAL · RGB OFF",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => OpenControlCenterProcess();

        _notificationPoller.StatusChanged += status =>
        {
            _notificationStatusText = status;
            Ui(() => _notificationStatusItem.Text = status);
        };
        _stateNormalizer.StateChanged += (state, transition) =>
        {
            UpdateNormalizedState(state, transition);
            _ = _rgbCanary.ApplyStateAsync(state, transition);
        };
        _rgbCanary.StatusChanged += UpdateRgbStatus;
        _deviceManager.StateChanged += UpdateDeviceStatus;
        RefreshDeviceMenu();

        _stateNormalizer.Start();
        _ = StartNotificationPollingAsync();
        _ = RestorePreferredDeviceAsync();
        RefreshTrackingIndicator();
        _ = Task.Run(() => StatusTrayIpc.RunServerAsync(HandleIpcAsync, _ipcCancellation.Token));

        if (!string.IsNullOrWhiteSpace(_config.LoadWarning))
            ShowBalloon(_config.LoadWarning);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var control = new ToolStripMenuItem("Открыть K15 Control Center")
        {
            Font = new Font(SystemFonts.MenuFont, FontStyle.Bold)
        };
        control.Click += (_, _) => OpenControlCenterProcess();
        menu.Items.Add(control);
        menu.Items.Add("Открыть Live Dashboard", null, (_, _) => OpenLiveDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_stateStatusItem);
        menu.Items.Add(_trackingStatusItem);
        menu.Items.Add(_notificationStatusItem);
        menu.Items.Add(_deviceStatusItem);
        menu.Items.Add(_rgbStatusItem);
        menu.Items.Add("✓ Сбросить WAITING / DONE", null, (_, _) => ManualResetAttention());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_rgbItem);
        menu.Items.Add(_deviceMenu);
        menu.Items.Add("Восстановить штатную подсветку текущего профиля", null,
            async (_, _) => await RestoreNativeLightingAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть Lighting Lab", null, async (_, _) => await OpenLightingLabAsync());
        menu.Items.Add("Открыть HID Research Lab", null, (_, _) => OpenSibling("Vorotex.K15.HidResearchLab.exe"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codexHookItem);
        menu.Items.Add(_loggingItem);
        menu.Items.Add("Открыть RGB config.toml", null, (_, _) => OpenPath(StatusLabConfig.FilePath));
        menu.Items.Add("Открыть RGB configurator", null, (_, _) => OpenConfigurator());
        menu.Items.Add("Открыть папку журнала", null, (_, _) => OpenPath(EventJournal.DirectoryPath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        return menu;
    }

    private async Task<StatusTrayIpcResponse> HandleIpcAsync(StatusTrayIpcRequest request)
    {
        var command = request.Command.Trim().ToLowerInvariant();
        return command switch
        {
            "snapshot" => await RunOnUiAsync(() => Task.FromResult(new StatusTrayIpcResponse(true, Snapshot: BuildSnapshot()))),
            "scan_devices" => await RunCommandAsync(ScanDevicesAsync),
            "connect_device" => await RunCommandAsync(() => ConnectDeviceAsync(request.Value)),
            "disconnect_device" => await RunCommandAsync(DisconnectDeviceAsync),
            "reconnect_device" => await RunCommandAsync(ReconnectDeviceAsync),
            "toggle_rgb" => await RunCommandAsync(ToggleRgbAsync),
            "reset_attention" => await RunCommandAsync(() => { ManualResetAttention(); return Task.CompletedTask; }),
            "restore_lighting" => await RunCommandAsync(RestoreNativeLightingAsync),
            "install_hooks" => await RunCommandAsync(InstallCodexHooksAsync),
            "toggle_logging" => await RunCommandAsync(() => { ToggleDetailedLogging(); return Task.CompletedTask; }),
            "set_autostart" => await RunCommandAsync(() =>
            {
                var enabled = string.Equals(request.Value, "true", StringComparison.OrdinalIgnoreCase);
                if (!SetAutostart(enabled))
                    throw new InvalidOperationException("Не удалось подтвердить изменение автозапуска Status Tray.");
                return Task.CompletedTask;
            }),
            "open_configurator" => await RunCommandAsync(() => { OpenConfigurator(); return Task.CompletedTask; }),
            "open_lighting_lab" => await RunCommandAsync(OpenLightingLabAsync),
            "open_hid_research" => await RunCommandAsync(() => { OpenSibling("Vorotex.K15.HidResearchLab.exe"); return Task.CompletedTask; }),
            "open_journal_folder" => await RunCommandAsync(() => { OpenPath(EventJournal.DirectoryPath); return Task.CompletedTask; }),
            _ => new StatusTrayIpcResponse(false, $"Неизвестная IPC-команда: {request.Command}")
        };
    }

    private async Task<StatusTrayIpcResponse> RunCommandAsync(Func<Task> command)
    {
        try
        {
            await RunOnUiAsync(command);
            return new StatusTrayIpcResponse(true, Snapshot: await RunOnUiAsync(() => Task.FromResult(BuildSnapshot())));
        }
        catch (Exception ex)
        {
            return new StatusTrayIpcResponse(false, ex.Message);
        }
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_trayIcon.ContextMenuStrip?.InvokeRequired != true)
            return action();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trayIcon.ContextMenuStrip.BeginInvoke(new Action(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        if (_trayIcon.ContextMenuStrip?.InvokeRequired != true)
            return action();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _trayIcon.ContextMenuStrip.BeginInvoke(new Action(async () =>
        {
            try
            {
                tcs.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        return tcs.Task;
    }

    private StatusTraySnapshot BuildSnapshot()
    {
        var hooks = CodexHookHealth.Inspect();
        var session = string.IsNullOrWhiteSpace(_stateNormalizer.FocusedSessionId)
            ? string.Empty
            : ShortSession(_stateNormalizer.FocusedSessionId);
        return new StatusTraySnapshot(
            JournalStateNormalizer.ToWireName(_stateNormalizer.State),
            _lastTransitionReason,
            _stateEnteredUtc,
            session,
            _stateNormalizer.FocusedCwd,
            _rgbCanary.Enabled,
            _rgbStatusText,
            _notificationStatusText,
            EventJournal.DetailedLoggingEnabled,
            hooks.Healthy,
            hooks.Status,
            hooks.Detail,
            StartupManager.IsEnabled(),
            StatusLabConfig.FilePath,
            _config.SchemaVersion,
            DeviceStateName(_deviceManager.ConnectionState),
            _deviceManager.SelectedDevice?.ProductString ?? string.Empty,
            _deviceManager.Candidates.Select(ToDeviceSnapshot).ToArray(),
            _stateNormalizer.SessionSnapshots.Select(session => new StatusTraySessionSnapshot(
                session.SessionId, JournalStateNormalizer.ToWireName(session.State), session.IsAlive,
                session.IsFocused, session.Cwd, session.ThreadId, session.TurnId,
                session.LastActivityUtc)).ToArray(),
            _stateNormalizer.AttentionSnapshot.RunningCount,
            _stateNormalizer.AttentionSnapshot.ApprovalWaitingCount,
            _stateNormalizer.AttentionSnapshot.DoneUnreadCount,
            _stateNormalizer.AttentionSnapshot.ActiveTaskSessionCount,
            _stateNormalizer.AttentionSnapshot.EndedSessionCount);
    }

    private async Task ScanDevicesAsync()
    {
        await _rgbCanary.DisableAsync("device_rescan");
        _deviceManager.Disconnect();
        await Task.Run(() => _deviceManager.Scan());
        UpdateDeviceStatus(_deviceManager.ConnectionState);
    }

    private async Task RestorePreferredDeviceAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                _deviceManager.Scan();
                _deviceManager.TryResolvePreferred();
            });
            UpdateDeviceStatus(_deviceManager.ConnectionState);
        }
        catch (Exception ex)
        {
            EventJournal.Append(new { timestampUtc = DateTimeOffset.UtcNow, source = "status_tray", @event = "device_startup_scan_failed", error = ex.GetType().Name });
        }
    }

    private Task ConnectDeviceAsync(string? candidateId)
    {
        if (string.IsNullOrWhiteSpace(candidateId) || !_deviceManager.SelectById(candidateId))
            throw new InvalidOperationException("Выбери устройство из списка кандидатов.");
        if (!_deviceManager.Connect())
            throw new InvalidOperationException("Не удалось подтвердить выбранное K15 устройство.");
        UpdateDeviceStatus(_deviceManager.ConnectionState);
        return Task.CompletedTask;
    }

    private async Task ReconnectDeviceAsync()
    {
        if (!_deviceManager.Reconnect())
            throw new InvalidOperationException("Выбранное K15 устройство недоступно для reconnect.");
        await Task.CompletedTask;
        UpdateDeviceStatus(_deviceManager.ConnectionState);
    }

    private async Task DisconnectDeviceAsync()
    {
        await _rgbCanary.DisableAsync("device_disconnect");
        _deviceManager.Disconnect();
        UpdateDeviceStatus(_deviceManager.ConnectionState);
    }

    private void UpdateDeviceStatus(K15DeviceConnectionState state)
    {
        Ui(() =>
        {
            var identity = _deviceManager.SelectedDevice?.ProductString;
            _deviceStatusItem.Text = string.IsNullOrWhiteSpace(identity)
                ? $"Устройство: {DeviceDisplayState(state)}"
                : $"Устройство: {identity} · {DeviceDisplayState(state)}";
            RefreshDeviceMenu();
            RefreshTrayTooltip(JournalStateNormalizer.ToWireName(_stateNormalizer.State));
        });
    }

    private void RefreshDeviceMenu()
    {
        foreach (ToolStripItem item in _deviceMenu.DropDownItems.Cast<ToolStripItem>().ToArray())
            item.Dispose();
        _deviceMenu.DropDownItems.Clear();
        _deviceMenu.DropDownItems.Add(new ToolStripMenuItem(
            $"Состояние: {DeviceStateName(_deviceManager.ConnectionState)}") { Enabled = false });
        _deviceMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var candidate in _deviceManager.Candidates)
        {
            var snapshot = ToDeviceSnapshot(candidate);
            var item = new ToolStripMenuItem(DeviceUxFormatting.CandidateLabel(snapshot));
            var candidateId = candidate.CandidateId;
            item.Click += async (_, _) => await ConnectDeviceAsync(candidateId);
            _deviceMenu.DropDownItems.Add(item);
        }

        if (_deviceManager.Candidates.Count == 0)
        {
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("Кандидаты не найдены") { Enabled = false });
        }

        _deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        var scanning = _deviceManager.ConnectionState == K15DeviceConnectionState.Scanning;
        var rescan = new ToolStripMenuItem("Пересканировать")
        {
            Enabled = !scanning
        };
        rescan.Click += async (_, _) => await ScanDevicesAsync();
        _deviceMenu.DropDownItems.Add(rescan);
        var reconnect = new ToolStripMenuItem("Переподключить")
        {
            Enabled = _deviceManager.SelectedDevice is not null && !scanning
        };
        reconnect.Click += async (_, _) => await ReconnectDeviceAsync();
        _deviceMenu.DropDownItems.Add(reconnect);
        var disconnect = new ToolStripMenuItem("Отключить")
        {
            Enabled = _deviceManager.ConnectionState is K15DeviceConnectionState.Connected or K15DeviceConnectionState.ConnectionLost
        };
        disconnect.Click += async (_, _) => await DisconnectDeviceAsync();
        _deviceMenu.DropDownItems.Add(disconnect);
    }

    private static string DeviceStateName(K15DeviceConnectionState state) => state.ToString().ToUpperInvariant();

    private static string DeviceDisplayState(K15DeviceConnectionState state) => state switch
    {
        K15DeviceConnectionState.Connected => "подключено",
        K15DeviceConnectionState.Scanning => "сканирование",
        K15DeviceConnectionState.ConnectionLost => "соединение потеряно",
        K15DeviceConnectionState.Error => "ошибка",
        _ => "не подключено"
    };

    private static StatusTrayDeviceCandidate ToDeviceSnapshot(K15DeviceCandidate candidate) =>
        new(candidate.CandidateId, DeviceUxFormatting.DisplayProduct(candidate.ProductString),
            $"{candidate.VendorId:X4}:{candidate.ProductId:X4}",
            $"{candidate.UsagePage:X4}:{candidate.Usage:X4}", candidate.FeatureReportLength,
            candidate.ProtocolVerified, candidate.VerificationResult ?? "not verified");

    private void OpenControlCenterProcess()
    {
        var path = DeviceUxFormatting.ResolveControlCenterPath(AppContext.BaseDirectory);
        if (path is null)
        {
            ShowBalloon("K15 Control Center не найден. Поддерживаются только варианты: рядом с Status Tray или в соседней папке control-center.");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenLiveDashboard()
    {
        const int port = 17815;
        var path = Path.Combine(AppContext.BaseDirectory, "Vorotex.K15.LiveDashboard.exe");
        if (!File.Exists(path))
        {
            EventJournal.Append(new { timestampUtc = DateTimeOffset.UtcNow, source = "status_tray", @event = "live_dashboard_missing" });
            ShowBalloon("Live Dashboard не найден рядом с Status Tray.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Process.Start(new ProcessStartInfo { FileName = $"http://127.0.0.1:{port}/", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            EventJournal.Append(new { timestampUtc = DateTimeOffset.UtcNow, source = "status_tray", @event = "live_dashboard_start_failed", exception = ex.GetType().Name, hresult = ex.HResult });
            ShowBalloon("Не удалось открыть Live Dashboard.");
        }
    }

    private void OpenSibling(string executable)
    {
        var path = Path.Combine(AppContext.BaseDirectory, executable);
        if (!File.Exists(path))
        {
            ShowBalloon($"{executable} не найден рядом с Status Tray. Приложения RC2 теперь распространяются отдельно.");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private bool SetAutostart(bool enabled)
    {
        try
        {
            var result = StartupManager.SetEnabled(enabled);
            ShowBalloon(result
                ? $"Автозапуск Status Tray: {(enabled ? "ВКЛ" : "ВЫКЛ")}."
                : "Не удалось подтвердить изменение автозапуска.");
            return result;
        }
        catch (Exception ex)
        {
            ShowBalloon("Автозапуск: " + ex.Message);
            return false;
        }
    }

    private void UpdateNormalizedState(K15NormalizedState state, StateTransition? transition)
    {
        var wire = JournalStateNormalizer.ToWireName(state);
        if (transition is not null)
        {
            _lastTransitionReason = transition.Reason;
            if (transition.Previous != transition.Current)
                _stateEnteredUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            _lastTransitionReason = "state_rehydrated";
            _stateEnteredUtc = DateTimeOffset.UtcNow;
        }

        Ui(() =>
        {
            _stateStatusItem.Text = string.IsNullOrWhiteSpace(_stateNormalizer.FocusedSessionId)
                ? $"Состояние: {wire}"
                : $"Состояние: {wire} · Codex {ShortSession(_stateNormalizer.FocusedSessionId)}";
            RefreshTrayTooltip(wire);
        });
    }

    private void ManualResetAttention()
    {
        var previous = JournalStateNormalizer.ToWireName(_stateNormalizer.State);
        _stateNormalizer.Acknowledge();
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_tray",
            @event = "manual_attention_reset",
            previous
        });
        ShowBalloon("Status Tray сбросил WAITING / DONE. Системные toast Windows не удаляются.");
    }

    private async Task RestoreNativeLightingAsync()
    {
        try
        {
            await _rgbCanary.RestoreCurrentAsync();
            ShowBalloon("Exact baseline текущего физического профиля восстановлен, если snapshot был доступен.");
        }
        catch (Exception ex)
        {
            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "k15_rgb",
                @event = "manual_baseline_restore_failed",
                exception = ex.GetType().FullName,
                message = ex.Message
            });
            ShowBalloon($"Не удалось восстановить штатную подсветку: {ex.Message}");
        }
    }

    private async Task ToggleRgbAsync()
    {
        if (_rgbCanary.Enabled)
        {
            _rgbItem.Enabled = false;
            try { await _rgbCanary.DisableAsync(); }
            finally
            {
                _rgbItem.Enabled = true;
                RefreshTrackingIndicator();
            }
            return;
        }

        var result = MessageBox.Show(
            "Включить физическую RGB-индикацию статусов K15?\n\n" +
            "Status Tray только наблюдает физически выбранный Profile A/B и не переключает его программно. " +
            "NORMAL восстанавливает exact onboard baseline. Закрой штатный VOROTEX перед обычной RGB-работой.\n\nПродолжить?",
            "VOROTEX K15 Status Tray",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        _rgbItem.Enabled = false;
        try
        {
            await _rgbCanary.EnableAsync(_stateNormalizer.State);
        }
        catch (Exception ex)
        {
            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "k15_rgb",
                @event = "rgb_enable_failed",
                exception = ex.GetType().FullName,
                message = ex.Message
            });
            ShowBalloon($"RGB-индикация не запущена: {ex.Message}");
        }
        finally
        {
            _rgbItem.Enabled = true;
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
            _rgbItem.Text = enabled ? "Выключить RGB-индикацию статусов" : "Включить RGB-индикацию статусов";
            RefreshTrayTooltip(JournalStateNormalizer.ToWireName(_stateNormalizer.State));
        });
    }

    private void RefreshTrayTooltip(string state)
    {
        var rgb = _rgbCanary.Enabled ? "RGB ON" : "RGB OFF";
        _trayIcon.Text = $"K15 Status Tray · {state} · {rgb}";
    }

    private void ToggleDetailedLogging()
    {
        EventJournal.SetDetailedLoggingEnabled(!EventJournal.DetailedLoggingEnabled);
        RefreshLoggingItem();
        ShowBalloon(EventJournal.DetailedLoggingEnabled
            ? "Подробный журнал включён. Размер всё равно ограничен ротацией."
            : "Подробный журнал выключен. Минимальные lifecycle events остаются для работы отслеживания.");
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
            ShowBalloon("Lighting Lab не найден рядом с Status Tray. Скачай его отдельным artifact.");
            return;
        }

        if (_rgbCanary.Enabled)
        {
            await _rgbCanary.DisableAsync("lighting_lab_launch");
            RefreshTrackingIndicator();
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void UpdateRgbStatus(string status)
    {
        _rgbStatusText = status;
        Ui(() =>
        {
            _rgbStatusItem.Text = status;
            RefreshTrackingIndicator();
        });
    }

    private async Task StartNotificationPollingAsync()
    {
        try
        {
            if (!await _notificationPoller.StartAsync())
                ShowBalloon("Доступ к уведомлениям не разрешён. Разреши его в Windows и перезапусти Status Tray.");
        }
        catch (Exception ex)
        {
            _notificationStatusText = $"Уведомления: ошибка 0x{ex.HResult:X8}";
            Ui(() => _notificationStatusItem.Text = _notificationStatusText);
        }
    }

    private async Task InstallCodexHooksAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "install-codex-hooks.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("install-codex-hooks.ps1 не найден рядом со Status Tray.", script);

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
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var errorText = (await stderr).Trim();
            var output = (await stdout).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                    ? $"Установщик hooks завершился с кодом {process.ExitCode}."
                    : errorText);

            using var document = JsonDocument.Parse(output);
            var count = document.RootElement.TryGetProperty("count", out var node) ? node.GetInt32() : 0;
            ShowBalloon($"Codex hooks обновлены в {count} окружение(я). Полностью перезапусти Codex.");
        }
        finally
        {
            _codexHookItem.Enabled = true;
        }
    }

    private void OpenConfigurator()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "configurator", "index.html");
        if (!File.Exists(path))
            throw new FileNotFoundException("RGB configurator не найден в artifact Status Tray.", path);

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
        if (Directory.Exists(path) || File.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ShowBalloon(string message)
    {
        _trayIcon.BalloonTipTitle = "VOROTEX K15 Status Tray";
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
        catch
        {
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _ipcCancellation.Cancel();
        await _notificationPoller.DisposeAsync();
        await _stateNormalizer.DisposeAsync();
        await _rgbCanary.DisposeAsync();
        _deviceManager.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _ipcCancellation.Cancel();
        _ipcCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trackingOnIcon.Dispose();
        _trackingOffIcon.Dispose();
        base.ExitThreadCore();
    }

    private static string ShortSession(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];
}
