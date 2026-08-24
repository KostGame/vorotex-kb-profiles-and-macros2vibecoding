using System.Diagnostics;

namespace Vorotex.K15.StatusLab;

internal sealed class StatusLabApplicationContext : ApplicationContext
{
    private readonly WindowsNotificationPoller _notificationPoller = new();
    private readonly NotifyIcon _trayIcon;
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

        _notificationStatusItem = new ToolStripMenuItem("Уведомления: запуск...")
        {
            Enabled = false
        };
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

        _ = StartNotificationPollingAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_notificationStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_codexHookItem);
        menu.Items.Add("Открыть журнал событий", null, (_, _) => OpenPath(EventJournal.FilePath));
        menu.Items.Add("Открыть папку журнала", null, (_, _) => OpenPath(EventJournal.DirectoryPath));
        menu.Items.Add("Очистить журнал", null, (_, _) => ClearJournal());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        return menu;
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
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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

            if (process.ExitCode == 0)
            {
                EventJournal.Append(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    source = "status_lab",
                    @event = "codex_hooks_installed"
                });
                ShowBalloon(string.IsNullOrWhiteSpace(stdout)
                    ? "Codex hooks установлены. Перезапусти Codex."
                    : stdout);
            }
            else
            {
                ShowBalloon(string.IsNullOrWhiteSpace(stderr)
                    ? $"Установщик hooks завершился с кодом {process.ExitCode}."
                    : stderr);
            }
        }
        catch (Exception ex)
        {
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
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
