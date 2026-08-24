using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

internal sealed class StatusLabApplicationContext : ApplicationContext
{
    private readonly WindowsNotificationPoller _notificationPoller = new();
    private readonly JournalStateNormalizer _stateNormalizer = new();
    private readonly K15RgbCanary _rgbCanary = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _stateStatusItem;
    private readonly ToolStripMenuItem _rgbStatusItem;
    private readonly ToolStripMenuItem _rgbCanaryItem;
    private readonly ToolStripMenuItem _notificationStatusItem;
    private readonly ToolStripMenuItem _codexHookItem;
    private bool _exiting;

    public StatusLabApplicationContext()
    {
        EventJournal.EnsureExists();
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "status_lab",
            @event = "started",
            version = typeof(StatusLabApplicationContext).Assembly.GetName().Version?.ToString()
        });

        _stateStatusItem = new ToolStripMenuItem("Состояние: NORMAL")
        {
            Enabled = false
        };
        _notificationStatusItem = new ToolStripMenuItem("Уведомления: запуск...")
        {
            Enabled = false
        };
        _rgbStatusItem = new ToolStripMenuItem("RGB: OFF")
        {
            Enabled = false
        };
        _rgbCanaryItem = new ToolStripMenuItem("Включить K15 RGB canary");
        _rgbCanaryItem.Click += async (_, _) => await ToggleRgbCanaryAsync();
        _codexHookItem = new ToolStripMenuItem("Установить Codex hooks");
        _codexHookItem.Click += async (_, _) => await InstallCodexHooksAsync();

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "VOROTEX K15 Status Lab",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notificationPoller.StatusChanged += status =>
        {
            try
            {
                if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
                {
                    _trayIcon.ContextMenuStrip.BeginInvoke(() => _notificationStatusItem.Text = status);
                }
                else
                {
                    _notificationStatusItem.Text = status;
                }
            }
            catch
            {
            }
        };

        _stateNormalizer.StateChanged += (state, transition) =>
        {
            UpdateNormalizedState(state, transition);
            _ = _rgbCanary.ApplyStateAsync(state);
        };
        _rgbCanary.StatusChanged += UpdateRgbStatus;
        _stateNormalizer.Start();
        _ = StartNotificationPollingAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateStatusItem);
        menu.Items.Add(_notificationStatusItem);
        menu.Items.Add(_rgbStatusItem);
        menu.Items.Add("Сбросить состояние в NORMAL", null, (_, _) => _stateNormalizer.Acknowledge());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_rgbCanaryItem);
        menu.Items.Add(_codexHookItem);
        menu.Items.Add("Открыть журнал событий", null, (_, _) => OpenPath(EventJournal.FilePath));
        menu.Items.Add("Открыть папку журнала", null, (_, _) => OpenPath(EventJournal.DirectoryPath));
        menu.Items.Add("Очистить журнал", null, (_, _) => ClearJournal());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        return menu;
    }

    private void UpdateNormalizedState(K15NormalizedState state, StateTransition? transition)
    {
        var wire = JournalStateNormalizer.ToWireName(state);

        void Apply()
        {
            _stateStatusItem.Text = $"Состояние: {wire}";
            _trayIcon.Text = $"K15 Status Lab · {wire}";
        }

        try
        {
            if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
                _trayIcon.ContextMenuStrip.BeginInvoke(Apply);
            else
                Apply();
        }
        catch
        {
        }
    }

    private async Task ToggleRgbCanaryAsync()
    {
        if (_rgbCanary.Enabled)
        {
            _rgbCanaryItem.Enabled = false;
            try
            {
                await _rgbCanary.DisableAsync();
                _rgbCanaryItem.Text = "Включить K15 RGB canary";
            }
            finally
            {
                _rgbCanaryItem.Enabled = true;
            }
            return;
        }

        var result = MessageBox.Show(
            "Включить физическую RGB-индикацию K15?\n\n" +
            "Canary меняет только lighting state через доказанный HID-протокол и сохраняет исходные байты для восстановления. " +
            "Закрой VOROTEX и W910 WebDriver перед тестом. " +
            "Переключать Profile A/B во время canary можно: новый профиль показывается своим цветом 5 секунд, затем возвращается текущее notification-состояние.\n\nПродолжить?",
            "VOROTEX K15 RGB canary",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            return;

        _rgbCanaryItem.Enabled = false;
        try
        {
            await _rgbCanary.EnableAsync(_stateNormalizer.State);
            _rgbCanaryItem.Text = "Выключить K15 RGB canary";
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
            ShowBalloon($"RGB canary не запущен: {ex.Message}");
        }
        finally
        {
            _rgbCanaryItem.Enabled = true;
        }
    }

    private void UpdateRgbStatus(string status)
    {
        void Apply()
        {
            _rgbStatusItem.Text = status;
            _rgbCanaryItem.Text = _rgbCanary.Enabled
                ? "Выключить K15 RGB canary"
                : "Включить K15 RGB canary";
        }

        try
        {
            if (_trayIcon.ContextMenuStrip?.InvokeRequired == true)
                _trayIcon.ContextMenuStrip.BeginInvoke(Apply);
            else
                Apply();
        }
        catch
        {
        }
    }

    private async Task StartNotificationPollingAsync()
    {
        try
        {
            var started = await _notificationPoller.StartAsync();
            if (!started)
                ShowBalloon("Доступ к уведомлениям не разрешен. Разреши его в системном диалоге Windows и перезапусти Status Lab.");
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

            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("PowerShell process did not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                ShowBalloon(string.IsNullOrWhiteSpace(stderr)
                    ? $"Установщик hooks завершился с кодом {process.ExitCode}."
                    : $"Не удалось установить hooks. Код {process.ExitCode}. Подробности записаны в журнал.");

                EventJournal.Append(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "status_lab",
                    @event = "codex_hooks_install_failed",
                    exitCode = process.ExitCode,
                    stderr
                });
                return;
            }

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var count = root.TryGetProperty("count", out var countNode) ? countNode.GetInt32() : 0;
            var paths = new List<string>();
            if (root.TryGetProperty("installed", out var installedNode) && installedNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in installedNode.EnumerateArray())
                {
                    if (item.TryGetProperty("hooksPath", out var pathNode))
                    {
                        var value = pathNode.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            paths.Add(value);
                    }
                }
            }

            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "status_lab",
                @event = "codex_hooks_installed",
                count,
                hooksPaths = paths
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
        finally
        {
            _codexHookItem.Enabled = true;
        }
    }

    private static void OpenPath(string path)
    {
        EventJournal.EnsureExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void ClearJournal()
    {
        var result = MessageBox.Show(
            "Очистить локальный диагностический журнал Status Lab?",
            "VOROTEX K15 Status Lab",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

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
        base.ExitThreadCore();
    }
}
