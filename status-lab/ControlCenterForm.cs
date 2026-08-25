using System.Diagnostics;

namespace Vorotex.K15.StatusLab;

internal sealed record ControlCenterSnapshot(
    string State,
    string Reason,
    DateTimeOffset StateSinceUtc,
    string Session,
    string Cwd,
    bool RgbEnabled,
    string RgbStatus,
    string NotificationStatus,
    bool DetailedLogging,
    CodexHookHealthSnapshot HookHealth,
    bool Autostart,
    string ConfigPath,
    int ConfigSchema);

internal sealed record ControlCenterActions(
    Func<Task> ToggleRgb,
    Action ResetAttention,
    Func<Task> RestoreLighting,
    Func<Task> InstallHooks,
    Action OpenConfigurator,
    Func<Task> OpenLightingLab,
    Action OpenJournalFolder,
    Func<bool, bool> SetAutostart);

internal sealed class ControlCenterForm : Form
{
    private readonly Func<ControlCenterSnapshot> _snapshotProvider;
    private readonly ControlCenterActions _actions;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

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
    private readonly Label _sleepResearch = ValueLabel();
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

    public ControlCenterForm(Func<ControlCenterSnapshot> snapshotProvider, ControlCenterActions actions)
    {
        _snapshotProvider = snapshotProvider;
        _actions = actions;

        Text = "VOROTEX K15 Control Center · RC2";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 680);
        Size = new Size(900, 820);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(13, 16, 22);
        ForeColor = Color.FromArgb(238, 243, 251);
        Font = new Font("Segoe UI", 9.5F);

        Controls.Add(BuildRoot());
        _timer.Tick += (_, _) => RefreshSnapshot();
        Shown += (_, _) =>
        {
            RefreshSnapshot();
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
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "VOROTEX K15 CONTROL CENTER",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 4)
        };
        var subtitle = new Label
        {
            Text = "RC2 · RGB notifier + Codex state + device recovery",
            AutoSize = true,
            ForeColor = Color.FromArgb(145, 158, 180),
            Margin = new Padding(0, 0, 0, 14)
        };
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        root.Controls.Add(header);

        var live = Card("Сейчас");
        live.Controls.Add(StatusGrid());
        root.Controls.Add(live);

        var actions = Card("Быстрые действия");
        actions.Controls.Add(ActionPanel());
        root.Controls.Add(actions);

        var sleep = Card("Сон клавиатуры · исследование протокола");
        sleep.Controls.Add(SleepResearchPanel());
        root.Controls.Add(sleep);

        var diagnosticsCard = Card("Диагностика · последние события");
        diagnosticsCard.Height = 260;
        diagnosticsCard.Controls.Add(_diagnostics);
        root.Controls.Add(diagnosticsCard);

        var footer = new Label
        {
            AutoSize = true,
            Text = "Sleep/power writes в RC2 отключены: сначала доказываем vendor field/protocol. DONE→NORMAL по простому открытию Codex пока не меняется.",
            ForeColor = Color.FromArgb(145, 158, 180),
            Padding = new Padding(2, 8, 2, 2)
        };
        root.Controls.Add(footer);
        return root;
    }

    private Control StatusGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 2) };
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
        AddRow(grid, "Автозапуск Windows", _autostart);
        AddRow(grid, "Config", _config);
        return grid;
    }

    private Control ActionPanel()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        flow.Controls.Add(Button("RGB ВКЛ / ВЫКЛ", async () => await _actions.ToggleRgb()));
        flow.Controls.Add(Button("✓ Сбросить WAITING / DONE", () => { _actions.ResetAttention(); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Восстановить подсветку", async () => await _actions.RestoreLighting()));
        flow.Controls.Add(Button("Проверить / обновить hooks", async () => await _actions.InstallHooks()));
        flow.Controls.Add(Button("Advanced RGB config", () => { _actions.OpenConfigurator(); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Lighting Lab", async () => await _actions.OpenLightingLab()));
        flow.Controls.Add(Button("Папка диагностики", () => { _actions.OpenJournalFolder(); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Обновить диагностику", () => { RefreshDiagnostics(); return Task.CompletedTask; }));
        flow.Controls.Add(Button("Автозапуск ВКЛ / ВЫКЛ", () =>
        {
            var current = StartupManager.IsEnabled();
            _actions.SetAutostart(!current);
            RefreshSnapshot();
            return Task.CompletedTask;
        }));
        return flow;
    }

    private Control SleepResearchPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        _sleepResearch.Text = "Протокол sleep/standby ещё не подтверждён. Status Lab НЕ пишет неизвестные power-команды.";
        _sleepResearch.ForeColor = Color.FromArgb(244, 198, 106);
        _sleepResearch.AutoSize = true;
        panel.Controls.Add(_sleepResearch);

        var instructions = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(800, 0),
            ForeColor = Color.FromArgb(170, 181, 198),
            Text = "1) Capture BEFORE.  2) В штатном VOROTEX измени только sleep/standby.  3) Capture AFTER. Полученный diff остаётся только локально. DeviceFeature.ini помечается как volatile, чтобы не принять шум за протокол."
        };
        panel.Controls.Add(instructions);

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 7, 0, 0) };
        flow.Controls.Add(Button("1 · Capture BEFORE", () =>
        {
            var result = DeviceSettingsResearch.CaptureBefore();
            _sleepResearch.Text = result.Message;
            return Task.CompletedTask;
        }));
        flow.Controls.Add(Button("Открыть VOROTEX", () =>
        {
            var exe = DeviceSettingsResearch.FindVendorExecutable();
            if (exe is null)
                MessageBox.Show(this, "Штатный VOROTEX-K15-PRO.exe не найден в Program Files (x86).", Text);
            else
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            return Task.CompletedTask;
        }));
        flow.Controls.Add(Button("2 · Capture AFTER + diff", () =>
        {
            var result = DeviceSettingsResearch.CaptureAfter();
            _sleepResearch.Text = result.Message;
            if (result.Success && result.ReportPath is not null)
                Process.Start(new ProcessStartInfo { FileName = result.ReportPath, UseShellExecute = true });
            return Task.CompletedTask;
        }));
        flow.Controls.Add(Button("Папка sleep research", () =>
        {
            Directory.CreateDirectory(DeviceSettingsResearch.RootDirectory);
            Process.Start(new ProcessStartInfo { FileName = DeviceSettingsResearch.RootDirectory, UseShellExecute = true });
            return Task.CompletedTask;
        }));
        panel.Controls.Add(flow);
        return panel;
    }

    private void RefreshSnapshot()
    {
        if (IsDisposed)
            return;
        try
        {
            var s = _snapshotProvider();
            _state.Text = s.State;
            _state.ForeColor = StateColor(s.State);
            _reason.Text = string.IsNullOrWhiteSpace(s.Reason) ? "—" : s.Reason;
            _elapsed.Text = FormatElapsed(DateTimeOffset.UtcNow - s.StateSinceUtc);
            _session.Text = string.IsNullOrWhiteSpace(s.Session) ? "—" : s.Session;
            _cwd.Text = string.IsNullOrWhiteSpace(s.Cwd) ? "—" : s.Cwd;
            _rgb.Text = s.RgbStatus;
            _notifications.Text = s.NotificationStatus;
            _hooks.Text = s.HookHealth.Status + " · " + s.HookHealth.Detail;
            _hooks.ForeColor = s.HookHealth.Healthy ? Color.FromArgb(121, 217, 154) : Color.FromArgb(244, 198, 106);
            _logging.Text = s.DetailedLogging ? "ВКЛ" : "ВЫКЛ · минимальные lifecycle events сохраняются";
            _autostart.Text = s.Autostart ? "ВКЛ" : "ВЫКЛ";
            _config.Text = $"schema v{s.ConfigSchema} · {s.ConfigPath}";
        }
        catch (Exception ex)
        {
            _reason.Text = "Control Center refresh: " + ex.Message;
        }
    }

    private void RefreshDiagnostics()
    {
        try
        {
            EventJournal.EnsureExists();
            var lines = ReadTail(EventJournal.FilePath, 24);
            var pretty = new List<string>();
            foreach (var line in lines)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var ts = GetString(r, "timestampUtc");
                    var source = GetString(r, "source");
                    var ev = GetString(r, "event");
                    var reason = GetString(r, "reason");
                    var current = GetString(r, "current");
                    var suffix = string.Join(" · ", new[] { current, reason }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    pretty.Add($"{ShortTime(ts),8}  {source,-20} {ev}{(suffix.Length > 0 ? " · " + suffix : string.Empty)}");
                }
                catch
                {
                }
            }
            _diagnostics.Lines = pretty.ToArray();
        }
        catch (Exception ex)
        {
            _diagnostics.Text = "Не удалось прочитать журнал: " + ex.Message;
        }
    }

    private static string[] ReadTail(string path, int count)
    {
        var queue = new Queue<string>(count);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == count)
                queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue.ToArray();
    }

    private static string GetString(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;

    private static string ShortTime(string value) => DateTimeOffset.TryParse(value, out var dt) ? dt.ToLocalTime().ToString("HH:mm:ss") : "--:--:--";
    private static string FormatElapsed(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private static Color StateColor(string state) => state switch
    {
        "RUNNING" => Color.FromArgb(110, 180, 255),
        "WAITING" => Color.FromArgb(255, 196, 90),
        "DONE_PENDING_ATTENTION" => Color.FromArgb(205, 150, 255),
        "ERROR" => Color.FromArgb(255, 110, 110),
        _ => Color.FromArgb(170, 181, 198)
    };

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
            catch (Exception ex) { MessageBox.Show(button.FindForm(), ex.Message, "VOROTEX K15 Control Center"); }
            finally { button.Enabled = true; }
        };
        return button;
    }

    private static Label ValueLabel(float size = 9.5F) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(225, 232, 242),
        Font = new Font("Segoe UI", size)
    };

    private static void AddRow(TableLayoutPanel grid, string name, Label value)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            AutoSize = true,
            Text = name,
            ForeColor = Color.FromArgb(145, 158, 180),
            Padding = new Padding(0, 3, 0, 3)
        }, 0, row);
        value.Padding = new Padding(0, 3, 0, 3);
        grid.Controls.Add(value, 1, row);
    }
}
