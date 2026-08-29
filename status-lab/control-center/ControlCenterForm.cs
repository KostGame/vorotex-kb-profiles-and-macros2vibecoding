using System.Diagnostics;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.ControlCenter;

internal sealed class ControlCenterForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private StatusTraySnapshot? _lastSnapshot;

    private readonly Label _connection = ValueLabel(11);
    private readonly Label _deviceState = ValueLabel(11);
    private readonly Label _deviceIdentity = ValueLabel();
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 620 };
    private string? _explicitCandidateId;
    private readonly Label _state = ValueLabel(20);
    private readonly Label _reason = ValueLabel();
    private readonly Label _elapsed = ValueLabel();
    private readonly Label _session = ValueLabel();
    private readonly Label _cwd = ValueLabel();
    private readonly Label _rgb = ValueLabel();
    private readonly Label _notifications = ValueLabel();
    private readonly Label _hooks = ValueLabel();
    private readonly Label _logging = ValueLabel();
    private readonly Label _autostart = ValueLabel();
    private readonly Label _config = ValueLabel();
    private readonly TextBox _diagnostics = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9F),
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(17, 20, 26),
        ForeColor = Color.FromArgb(210, 220, 234),
        BorderStyle = BorderStyle.FixedSingle
    };

    public ControlCenterForm()
    {
        Text = "VOROTEX K15 Control Center · RC2";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 650);
        Size = new Size(920, 760);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(13, 16, 22);
        ForeColor = Color.FromArgb(238, 243, 251);
        Font = new Font("Segoe UI", 9.5F);
        Controls.Add(BuildRoot());

        _devices.SelectionChangeCommitted += (_, _) =>
        {
            _explicitCandidateId = _devices.SelectedValue as string;
        };

        _timer.Tick += async (_, _) => await RefreshSnapshotAsync();
        Shown += async (_, _) =>
        {
            await RefreshSnapshotAsync();
            RefreshDiagnostics();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Stop();
    }

    private Control BuildRoot()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 6,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        header.Controls.Add(new Label
        {
            Text = "VOROTEX K15 CONTROL CENTER",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 4)
        });
        header.Controls.Add(new Label
        {
            Text = "RC2 · отдельный UI-процесс · Status Tray остаётся владельцем RGB/state",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 158, 180),
            Margin = new Padding(0, 0, 0, 5)
        });
        _connection.AutoSize = true;
        header.Controls.Add(_connection);
        root.Controls.Add(header);

        var device = Card("Устройство / Device");
        device.Controls.Add(DevicePanel());
        root.Controls.Add(device);

        var live = Card("Сейчас");
        live.Controls.Add(StatusGrid());
        root.Controls.Add(live);

        var actions = Card("Управление");
        actions.Controls.Add(ActionPanel());
        root.Controls.Add(actions);

        var diagnostics = Card("Диагностика · последние события");
        diagnostics.Height = 255;
        diagnostics.Controls.Add(_diagnostics);
        root.Controls.Add(diagnostics);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Lighting Lab и HID Research Lab теперь отдельные приложения. Control Center сам не открывает K15 и не пишет HID.",
            ForeColor = Color.FromArgb(145, 158, 180),
            Padding = new Padding(2, 8, 2, 2)
        });
        return root;
    }

    private Control StatusGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 4, 0, 2)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, "Состояние", _state);
        AddRow(grid, "Причина", _reason);
        AddRow(grid, "В этом состоянии", _elapsed);
        AddRow(grid, "Codex session", _session);
        AddRow(grid, "Рабочая папка", _cwd);
        AddRow(grid, "RGB", _rgb);
        AddRow(grid, "Windows notifications", _notifications);
        AddRow(grid, "Codex hooks", _hooks);
        AddRow(grid, "Подробный журнал", _logging);
        AddRow(grid, "Автозапуск Tray", _autostart);
        AddRow(grid, "Config", _config);
        return grid;
    }

    private Control ActionPanel()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        flow.Controls.Add(Button("Запустить Status Tray", () => { StartSibling("Vorotex.K15.StatusTray.exe"); return Task.CompletedTask; }));
        flow.Controls.Add(Button("RGB ВКЛ / ВЫКЛ", () => SendCommandAsync("toggle_rgb")));
        flow.Controls.Add(Button("✓ Сбросить WAITING / DONE", () => SendCommandAsync("reset_attention")));
        flow.Controls.Add(Button("Восстановить подсветку", () => SendCommandAsync("restore_lighting")));
        flow.Controls.Add(Button("Проверить / обновить hooks", () => SendCommandAsync("install_hooks")));
        flow.Controls.Add(Button("Подробный журнал ВКЛ / ВЫКЛ", () => SendCommandAsync("toggle_logging")));
        flow.Controls.Add(Button("Автозапуск Tray ВКЛ / ВЫКЛ", async () =>
        {
            var enabled = _lastSnapshot?.Autostart != true;
            await SendCommandAsync("set_autostart", enabled ? "true" : "false");
        }));
        flow.Controls.Add(Button("Advanced RGB config", () => SendCommandAsync("open_configurator")));
        flow.Controls.Add(Button("Lighting Lab", () => { StartSibling("Vorotex.K15.LightingLab.exe"); return Task.CompletedTask; }));
        flow.Controls.Add(Button("HID Research Lab", () => { StartSibling("Vorotex.K15.HidResearchLab.exe"); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Папка диагностики", () => { OpenPath(JournalDirectory); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Обновить", async () => { await RefreshSnapshotAsync(); RefreshDiagnostics(); }));
        return flow;
    }

    private Control DevicePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(panel, "Состояние устройства", _deviceState);
        AddRow(panel, "Выбранное устройство", _deviceIdentity);
        AddRow(panel, "Кандидат", _devices);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actions.Controls.Add(Button("Сканировать / обновить", () => SendCommandAsync("scan_devices")));
        actions.Controls.Add(Button("Подключить выбранное", () =>
        {
            if (string.IsNullOrWhiteSpace(_explicitCandidateId))
                throw new InvalidOperationException("Сначала явно выбери устройство в списке кандидатов.");
            return SendCommandAsync("connect_device", _explicitCandidateId);
        }));
        actions.Controls.Add(Button("Отключить", () => SendCommandAsync("disconnect_device")));
        actions.Controls.Add(Button("Reconnect", () => SendCommandAsync("reconnect_device")));
        AddRow(panel, "Действия", actions);
        return panel;
    }

    private async Task SendCommandAsync(string command, string? value = null)
    {
        try
        {
            var response = await StatusTrayIpc.SendAsync(command, value, timeoutMs: 8000);
            if (!response.Success)
                throw new InvalidOperationException(response.Error ?? "Status Tray отклонил команду.");
            if (response.Snapshot is not null)
                ApplySnapshot(response.Snapshot);
        }
        catch (Exception ex)
        {
            _connection.Text = "Status Tray: НЕТ СВЯЗИ · " + ex.Message;
            _connection.ForeColor = Color.FromArgb(255, 120, 120);
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        try
        {
            var response = await StatusTrayIpc.SendAsync("snapshot");
            if (!response.Success || response.Snapshot is null)
                throw new InvalidOperationException(response.Error ?? "snapshot недоступен");
            ApplySnapshot(response.Snapshot);
            _connection.Text = "Status Tray: подключён через local IPC";
            _connection.ForeColor = Color.FromArgb(121, 217, 154);
        }
        catch
        {
            _connection.Text = "Status Tray: не запущен или недоступен";
            _connection.ForeColor = Color.FromArgb(244, 198, 106);
        }
    }

    private void ApplySnapshot(StatusTraySnapshot s)
    {
        _lastSnapshot = s;
        _deviceState.Text = s.DeviceState;
        _deviceIdentity.Text = string.IsNullOrWhiteSpace(s.DeviceIdentity) ? "не подключено" : s.DeviceIdentity;
        var candidates = (s.DeviceCandidates ?? Array.Empty<StatusTrayDeviceCandidate>()).ToList();
        var preservedExplicitId = _explicitCandidateId;
        _devices.DisplayMember = nameof(DeviceChoice.Label);
        _devices.ValueMember = nameof(DeviceChoice.CandidateId);
        _devices.DataSource = candidates.Select(c => new DeviceChoice(
            c.CandidateId,
            DeviceUxFormatting.CandidateLabel(c))).ToList();
        _devices.SelectedIndex = -1;
        if (!string.IsNullOrWhiteSpace(preservedExplicitId) &&
            candidates.Any(c => c.CandidateId == preservedExplicitId))
        {
            _explicitCandidateId = preservedExplicitId;
            _devices.SelectedValue = preservedExplicitId;
        }
        else
        {
            _explicitCandidateId = null;
        }
        _state.Text = s.State;
        _state.ForeColor = StateColor(s.State);
        _reason.Text = string.IsNullOrWhiteSpace(s.Reason) ? "—" : s.Reason;
        _elapsed.Text = FormatElapsed(DateTimeOffset.UtcNow - s.StateSinceUtc);
        _session.Text = string.IsNullOrWhiteSpace(s.Session) ? "—" : s.Session;
        _cwd.Text = string.IsNullOrWhiteSpace(s.Cwd) ? "—" : s.Cwd;
        _rgb.Text = s.RgbStatus;
        _notifications.Text = s.NotificationStatus;
        _hooks.Text = s.HooksStatus + " · " + s.HooksDetail;
        _hooks.ForeColor = s.HooksHealthy ? Color.FromArgb(121, 217, 154) : Color.FromArgb(244, 198, 106);
        _logging.Text = s.DetailedLogging ? "ВКЛ" : "ВЫКЛ · lifecycle минимум остаётся";
        _autostart.Text = s.Autostart ? "ВКЛ" : "ВЫКЛ";
        _config.Text = $"schema v{s.ConfigSchema} · {s.ConfigPath}";
    }

    private sealed record DeviceChoice(string CandidateId, string Label);

    private void RefreshDiagnostics()
    {
        try
        {
            if (!File.Exists(JournalPath))
            {
                _diagnostics.Text = "Журнал ещё не создан Status Tray.";
                return;
            }
            var queue = new Queue<string>(24);
            using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count == 24)
                    queue.Dequeue();
                queue.Enqueue(line);
            }
            _diagnostics.Lines = queue.ToArray();
        }
        catch (Exception ex)
        {
            _diagnostics.Text = "Не удалось прочитать журнал: " + ex.Message;
        }
    }

    private static string JournalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOROTEX", "K15 Status Lab");
    private static string JournalPath => Path.Combine(JournalDirectory, "events.jsonl");

    private static void StartSibling(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            MessageBox.Show(
                $"{file} не найден рядом с Control Center.\n\nВ RC2 приложения скачиваются отдельными artifact и могут быть положены в одну папку.",
                "VOROTEX K15 Control Center",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static Panel Card(string title)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Color.FromArgb(23, 27, 35)
        };
        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = title,
            Font = new Font("Segoe UI Semibold", 12F),
            ForeColor = Color.White,
            Padding = new Padding(0, 0, 0, 8)
        });
        return panel;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            AutoSize = true,
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(31, 39, 54),
            ForeColor = Color.FromArgb(238, 243, 251),
            Padding = new Padding(9, 5, 9, 5),
            Margin = new Padding(0, 0, 8, 8),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(61, 73, 94);
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await action(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VOROTEX K15 Control Center",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { button.Enabled = true; }
        };
        return button;
    }

    private static Label ValueLabel(float size = 9.5F) => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", size),
        ForeColor = Color.FromArgb(230, 235, 245),
        MaximumSize = new Size(680, 0)
    };

    private static void AddRow(TableLayoutPanel grid, string name, Control value)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            ForeColor = Color.FromArgb(157, 177, 211),
            Margin = new Padding(0, 5, 8, 5)
        }, 0, row);
        value.Margin = new Padding(0, 5, 0, 5);
        grid.Controls.Add(value, 1, row);
    }

    private static string FormatElapsed(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private static Color StateColor(string state) => state switch
    {
        "RUNNING" => Color.FromArgb(110, 180, 255),
        "WAITING" => Color.FromArgb(255, 196, 90),
        "DONE_PENDING_ATTENTION" => Color.FromArgb(205, 150, 255),
        "ERROR" => Color.FromArgb(255, 110, 110),
        _ => Color.FromArgb(170, 181, 198)
    };
}
