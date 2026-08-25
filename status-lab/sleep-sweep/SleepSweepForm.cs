using System.Diagnostics;

namespace Vorotex.K15.SleepSweepLab;

internal sealed class SleepSweepForm : Form
{
    private readonly SleepSweepSession _session = new();
    private readonly Dictionary<int, Button> _captureButtons = new();
    private readonly Label _sessionLabel = new();
    private readonly Label _progressLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _profileLabel = new();

    public SleepSweepForm()
    {
        Text = "VOROTEX K15 Sleep Sweep Lab";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(820, 580);
        BackColor = Color.FromArgb(13, 17, 23);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "VOROTEX K15 SLEEP SWEEP LAB",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 19f),
            ForeColor = Color.White,
            Location = new Point(22, 18)
        };

        var subtitle = new Label
        {
            Text = "Read-only research tool · 1 → 10 minutes · no HID writes",
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 180, 230),
            Location = new Point(25, 56)
        };

        var instructions = new Label
        {
            Text = "Держи штатный VOROTEX открытым и НЕ переключай Profile A/B.\n" +
                   "Поставь Sleep = 1 min → нажми Capture 1. Затем 2 min → Capture 2 … до 10 min.",
            AutoSize = false,
            Size = new Size(810, 54),
            Location = new Point(25, 92),
            ForeColor = Color.FromArgb(220, 220, 220)
        };

        _sessionLabel.Location = new Point(25, 151);
        _sessionLabel.Size = new Size(810, 24);
        _sessionLabel.ForeColor = Color.FromArgb(170, 190, 220);

        _profileLabel.Location = new Point(25, 176);
        _profileLabel.Size = new Size(810, 24);
        _profileLabel.ForeColor = Color.FromArgb(255, 205, 100);

        var grid = new TableLayoutPanel
        {
            Location = new Point(25, 214),
            Size = new Size(810, 160),
            ColumnCount = 5,
            RowCount = 2,
            BackColor = Color.FromArgb(22, 27, 34),
            Padding = new Padding(12)
        };
        for (var col = 0; col < 5; col++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        for (var minute = 1; minute <= 10; minute++)
        {
            var capturedMinute = minute;
            var button = new Button
            {
                Text = $"Capture {minute}\n{minute} min",
                Dock = DockStyle.Fill,
                Margin = new Padding(6),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 42, 52),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10.5f),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(75, 95, 125);
            button.Click += (_, _) => Capture(capturedMinute);
            _captureButtons[minute] = button;
            grid.Controls.Add(button, (minute - 1) % 5, (minute - 1) / 5);
        }

        _progressLabel.Location = new Point(25, 389);
        _progressLabel.Size = new Size(810, 25);
        _progressLabel.Font = new Font("Segoe UI Semibold", 10f);

        _statusLabel.Location = new Point(25, 418);
        _statusLabel.Size = new Size(810, 55);
        _statusLabel.ForeColor = Color.FromArgb(190, 210, 235);

        var openVorotex = MakeButton("Открыть штатный VOROTEX", 25, 491, 190);
        openVorotex.Click += (_, _) => OpenVendor();

        var openReport = MakeButton("Открыть report.json", 225, 491, 175);
        openReport.Click += (_, _) => OpenPath(_session.ReportPath);

        var openFolder = MakeButton("Открыть папку с результатом", 410, 491, 220);
        openFolder.Click += (_, _) => OpenPath(_session.SessionDirectory);

        var reset = MakeButton("Новая серия 1→10", 640, 491, 195);
        reset.BackColor = Color.FromArgb(67, 38, 43);
        reset.Click += (_, _) => ResetSession();

        var footer = new Label
        {
            Text = "Когда станет 10/10: пришли мне только sleep-sweep-report.json. Raw copies остаются локально.",
            Location = new Point(25, 548),
            Size = new Size(810, 35),
            ForeColor = Color.FromArgb(140, 155, 175)
        };

        Controls.AddRange([
            title, subtitle, instructions, _sessionLabel, _profileLabel,
            grid, _progressLabel, _statusLabel,
            openVorotex, openReport, openFolder, reset, footer
        ]);

        RefreshUi("Готово. Начни с 1 минуты.");
    }

    private static Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 38),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 42, 52),
        ForeColor = Color.White,
        Cursor = Cursors.Hand
    };

    private void Capture(int minute)
    {
        if (_session.CapturedMinutes.Contains(minute))
        {
            var replace = MessageBox.Show(
                $"Snapshot для {minute} min уже есть. Переснять его?",
                "VOROTEX K15 Sleep Sweep Lab",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (replace != DialogResult.Yes)
                return;
        }

        SetButtonsEnabled(false);
        try
        {
            var outcome = _session.Capture(minute);
            RefreshUi(outcome.Message, outcome.Success ? outcome.CurrentProfile : null);
            if (!outcome.Success)
                MessageBox.Show(outcome.Message, "Sleep Sweep Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (_session.Complete)
                MessageBox.Show(
                    "Все 10 snapshot'ов готовы. Пришли sleep-sweep-report.json.",
                    "Sleep Sweep complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void RefreshUi(string status, int? currentProfile = null)
    {
        var captured = _session.CapturedMinutes.ToHashSet();
        foreach (var pair in _captureButtons)
        {
            var isCaptured = captured.Contains(pair.Key);
            pair.Value.Text = isCaptured
                ? $"✓ {pair.Key} min\nCaptured"
                : $"Capture {pair.Key}\n{pair.Key} min";
            pair.Value.BackColor = isCaptured
                ? Color.FromArgb(30, 82, 62)
                : Color.FromArgb(35, 42, 52);
            pair.Value.FlatAppearance.BorderColor = isCaptured
                ? Color.FromArgb(72, 180, 130)
                : Color.FromArgb(75, 95, 125);
        }

        var sessionName = Path.GetFileName(_session.SessionDirectory);
        _sessionLabel.Text = $"Session: {sessionName}   ·   {_session.SessionDirectory}";
        _progressLabel.Text = $"Прогресс: {captured.Count}/10   ·   снято: {(captured.Count == 0 ? "—" : string.Join(", ", captured.OrderBy(value => value)))}";
        _statusLabel.Text = status;

        if (currentProfile.HasValue)
        {
            _profileLabel.Text = currentProfile.Value switch
            {
                0 => "Последний capture: Profile A / slot 0",
                1 => "Последний capture: Profile B / slot 1",
                _ => $"Последний capture: slot {currentProfile.Value}"
            };
        }
        else
        {
            var knownProfiles = _session.State.Snapshots
                .Where(snapshot => snapshot.CurrentProfile.HasValue)
                .Select(snapshot => snapshot.CurrentProfile!.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            _profileLabel.Text = knownProfiles.Length switch
            {
                0 => "Profile: пока не определён",
                1 => knownProfiles[0] == 0 ? "Profile серии: A / slot 0" : knownProfiles[0] == 1 ? "Profile серии: B / slot 1" : $"Profile серии: slot {knownProfiles[0]}",
                _ => $"⚠ В серии обнаружена смена профиля: {string.Join(", ", knownProfiles)}"
            };
        }
    }

    private void ResetSession()
    {
        var result = MessageBox.Show(
            "Начать новую чистую серию 1→10? Текущая папка и отчёт останутся на диске.",
            "New sleep sweep",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
            return;
        _session.Reset();
        RefreshUi("Новая серия создана. В VOROTEX выставь 1 минуту и нажми Capture 1.");
    }

    private static void OpenVendor()
    {
        var path = SleepSweepSession.FindVendorExecutable();
        if (path is null)
        {
            MessageBox.Show("VOROTEX-K15-PRO.exe не найден.", "Sleep Sweep Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show($"Путь не найден:\n{path}", "Sleep Sweep Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (var button in _captureButtons.Values)
            button.Enabled = enabled;
    }
}
