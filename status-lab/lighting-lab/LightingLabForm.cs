using System.Diagnostics;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.LightingLab;

internal sealed class LightingLabForm : Form
{
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _brightness = new() { Minimum = 1, Maximum = 6, Value = 6 };
    private readonly NumericUpDown _speed = new() { Minimum = 1, Maximum = 7, Value = 4 };
    private readonly ComboBox _direction = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _wire = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _profile = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    private readonly Label _colorModel = new() { AutoSize = true, ForeColor = Color.DarkGoldenrod };
    private readonly CheckBox[] _paletteChecks = new CheckBox[7];
    private readonly TextBox[] _paletteColors = new TextBox[7];
    private readonly CheckBox _autoRestore = new() { Text = "Auto restore", Checked = true, AutoSize = true };
    private readonly NumericUpDown _restoreSeconds = new() { Minimum = 1, Maximum = 30, Value = 4 };
    private readonly TextBox _note = new() { Multiline = true, Height = 58, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _activity = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 150 };
    private readonly Button _apply = new() { Text = "Apply test", AutoSize = true };
    private readonly Button _restore = new() { Text = "Restore exact baseline", AutoSize = true };
    private readonly Button _saveNote = new() { Text = "Save note for last test", AutoSize = true };
    private readonly Button _openLog = new() { Text = "Open JSONL log", AutoSize = true };
    private readonly System.Windows.Forms.Timer _profileTimer = new() { Interval = 500 };

    private LightingLabSession? _session;
    private LightingLabTestResult? _lastTest;

    private static readonly string[] DefaultPalette =
    [
        "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#800080", "#00FFFF", "#FFFFFF"
    ];

    public LightingLabForm()
    {
        Text = "VOROTEX K15 Lighting Lab";
        Width = 820;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 700);

        _direction.Items.AddRange(["0 · Left / backward", "1 · Right / forward"]);
        _direction.SelectedIndex = 0;
        _wire.Items.AddRange(["RGB", "GRB"]);
        _wire.SelectedIndex = 0;

        foreach (var mode in Enum.GetValues<K15LightingMode>())
            _mode.Items.Add(new ModeChoice(mode, LightingLabSession.UiModeName(mode)));
        _mode.SelectedIndex = Array.IndexOf(Enum.GetValues<K15LightingMode>(), K15LightingMode.SingleColorBreathing);
        _mode.SelectedIndexChanged += (_, _) => UpdateColorModelUi();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 10
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildHeader());
        root.Controls.Add(BuildSettings());
        root.Controls.Add(BuildPalette());
        root.Controls.Add(BuildActions());
        root.Controls.Add(new Label { Text = "Комментарий к тесту", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        root.Controls.Add(_note);
        root.Controls.Add(_saveNote);
        root.Controls.Add(new Label { Text = "Activity", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        root.Controls.Add(_activity);
        root.Controls.Add(_openLog);

        _apply.Click += async (_, _) => await ApplyAsync();
        _restore.Click += (_, _) => RestoreNow("manual_restore");
        _saveNote.Click += (_, _) => SaveNote();
        _openLog.Click += (_, _) => OpenLog();
        _profileTimer.Tick += (_, _) => RefreshProfileLabel();
        FormClosed += (_, _) => _session?.Dispose();
        Shown += (_, _) => StartSession();

        UpdateColorModelUi();
    }

    private Control BuildHeader()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        panel.Controls.Add(new Label
        {
            Text = "K15 Lighting Lab",
            AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 18, 0)
        });
        panel.Controls.Add(_profile);
        panel.Controls.Add(new Label
        {
            Text = "Только наблюдает физический Profile A/B. Программных переключений профиля нет.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(15, 8, 0, 0)
        });
        return panel;
    }

    private Control BuildSettings()
    {
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4, Padding = new Padding(0, 12, 0, 8) };
        for (var i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        AddField(grid, 0, "Mode", _mode);
        AddField(grid, 1, "Brightness 1..6", _brightness);
        AddField(grid, 2, "Speed 1..7", _speed);
        AddField(grid, 3, "Direction", _direction);
        AddField(grid, 4, "Wire order", _wire);
        grid.SetColumnSpan(_colorModel, 3);
        grid.Controls.Add(_colorModel, 1, 2);
        return grid;
    }

    private Control BuildPalette()
    {
        var box = new GroupBox { Text = "Color / Palette bytes", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 7 };
        for (var i = 0; i < 7; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14.28f));
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            _paletteChecks[i] = new CheckBox { Text = $"#{i + 1}", Checked = i == 0, AutoSize = true };
            _paletteColors[i] = new TextBox { Text = DefaultPalette[i], Width = 88 };
            var pick = new Button { Text = "Pick", Width = 58, Height = 25 };
            var index = i;
            pick.Click += (_, _) => PickColor(index);
            cell.Controls.Add(_paletteChecks[i]);
            cell.Controls.Add(_paletteColors[i]);
            cell.Controls.Add(pick);
            grid.Controls.Add(cell, i, 0);
        }
        box.Controls.Add(grid);
        return box;
    }

    private Control BuildActions()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 8) };
        panel.Controls.Add(_apply);
        panel.Controls.Add(_restore);
        panel.Controls.Add(_autoRestore);
        panel.Controls.Add(new Label { Text = "after sec:", AutoSize = true, Margin = new Padding(8, 8, 2, 0) });
        _restoreSeconds.Width = 55;
        panel.Controls.Add(_restoreSeconds);
        return panel;
    }

    private static void AddField(TableLayoutPanel grid, int index, string label, Control control)
    {
        var col = index % 4;
        var row = (index / 4) * 2;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.DimGray }, col, row);
        control.Dock = DockStyle.Top;
        grid.Controls.Add(control, col, row + 1);
    }

    private void StartSession()
    {
        try
        {
            _session = new LightingLabSession();
            AppendActivity($"Lab started. Log: {_session.LogPath}");
            RefreshProfileLabel();
            _profileTimer.Start();
        }
        catch (Exception ex)
        {
            _apply.Enabled = false;
            _restore.Enabled = false;
            AppendActivity("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "K15 Lighting Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ApplyAsync()
    {
        if (_session is null)
            return;

        try
        {
            var choice = (ModeChoice)_mode.SelectedItem!;
            var request = BuildRequest(choice.Mode);
            _lastTest = _session.Apply(request);
            AppendActivity($"{DateTime.Now:HH:mm:ss.fff} TEST {_lastTest.TestId} · Profile {_lastTest.Profile} · {_lastTest.Mode} · 0x{_lastTest.ModeCode:X2} · PASS");

            if (_autoRestore.Checked)
            {
                var slot = _lastTest.OnboardSlot;
                var seconds = (int)_restoreSeconds.Value;
                AppendActivity($"Auto restore scheduled in {seconds}s for profile {ProfileName(slot)}.");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (_session is not null && _session.ReadActiveSlot() == slot)
                    RestoreNow("auto_restore");
                else
                    AppendActivity("Auto restore deferred: physical profile changed. It will be restored when tested/restored while active.");
            }
        }
        catch (Exception ex)
        {
            AppendActivity("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Lighting test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private LightingLabTestRequest BuildRequest(K15LightingMode mode)
    {
        var selectedMask = (byte)0;
        for (var i = 0; i < 7; i++)
            if (_paletteChecks[i].Checked)
                selectedMask |= (byte)(1 << i);

        string[] colors;
        byte mask;
        switch (LightingLabSession.ColorModel(mode))
        {
            case "single_explicit_color":
                colors = [_paletteColors[0].Text.Trim()];
                mask = 0x01;
                break;
            case "palette_plus_mask":
                if (selectedMask == 0)
                    throw new InvalidOperationException("Выбери хотя бы один цвет палитры.");
                colors = _paletteColors.Select(box => box.Text.Trim()).ToArray();
                mask = selectedMask;
                break;
            case "none":
                colors = [];
                mask = 0;
                break;
            default:
                // OEM UI does not expose a color for these modes. We still write one deterministic seed
                // record so the exact bytes are known in the log; firmware may ignore them entirely.
                colors = [_paletteColors[0].Text.Trim()];
                mask = 0x01;
                break;
        }

        foreach (var color in colors)
            _ = StatusLabConfig.ParseColor(color);

        return new LightingLabTestRequest(
            mode,
            (int)_brightness.Value,
            (int)_speed.Value,
            _direction.SelectedIndex,
            colors,
            mask,
            _wire.SelectedIndex == 0 ? WireColorOrder.RGB : WireColorOrder.GRB,
            _note.Text.Trim());
    }

    private void RestoreNow(string reason)
    {
        if (_session is null)
            return;
        try
        {
            _session.RestoreCurrent(reason);
            AppendActivity($"{DateTime.Now:HH:mm:ss.fff} exact baseline restored for active profile.");
        }
        catch (Exception ex)
        {
            AppendActivity("RESTORE ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveNote()
    {
        if (_session is null || _lastTest is null)
        {
            MessageBox.Show(this, "Сначала запусти тест.", "Lighting Lab");
            return;
        }
        _session.AddUserNote(_lastTest.TestId, _note.Text);
        AppendActivity($"Note saved for {_lastTest.TestId}.");
    }

    private void OpenLog()
    {
        if (_session is null)
            return;
        Process.Start(new ProcessStartInfo { FileName = _session.LogPath, UseShellExecute = true });
    }

    private void RefreshProfileLabel()
    {
        if (_session is null)
            return;
        try
        {
            var slot = _session.ReadActiveSlot();
            _profile.Text = $"Profile {ProfileName(slot)} · slot {slot}";
        }
        catch (Exception ex)
        {
            _profile.Text = "Profile ? · " + ex.Message;
        }
    }

    private void UpdateColorModelUi()
    {
        if (_mode.SelectedItem is not ModeChoice choice)
            return;
        var model = LightingLabSession.ColorModel(choice.Mode);
        _colorModel.Text = model switch
        {
            "single_explicit_color" => "OEM model: один явный Color. Используется palette #1.",
            "palette_plus_mask" => "OEM model: Palette + selection mask. Можно включать 1..7 позиций.",
            "oem_internal_or_unknown_palette" => "OEM UI не задаёт Color. Вероятна внутренняя палитра; seed bytes логируются как эксперимент.",
            "none" => "Off: цвет не используется.",
            _ => model
        };

        var paletteMode = model == "palette_plus_mask";
        var singleMode = model == "single_explicit_color";
        for (var i = 0; i < 7; i++)
        {
            _paletteChecks[i].Enabled = paletteMode;
            _paletteColors[i].Enabled = paletteMode || (singleMode && i == 0) || (model == "oem_internal_or_unknown_palette" && i == 0);
            if (singleMode)
                _paletteChecks[i].Checked = i == 0;
        }
    }

    private void PickColor(int index)
    {
        using var dialog = new ColorDialog { FullOpen = true };
        try { dialog.Color = ColorTranslator.FromHtml(_paletteColors[index].Text); } catch { }
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _paletteColors[index].Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void AppendActivity(string text)
    {
        _activity.AppendText(text + Environment.NewLine);
        _activity.SelectionStart = _activity.TextLength;
        _activity.ScrollToCaret();
    }

    private static string ProfileName(byte slot) => slot switch { 0 => "A", 1 => "B", _ => (slot + 1).ToString() };

    private sealed record ModeChoice(K15LightingMode Mode, string Title)
    {
        public override string ToString() => Title;
    }
}
