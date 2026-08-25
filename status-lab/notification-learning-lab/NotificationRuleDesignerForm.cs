using Vorotex.K15.StatusLab;

namespace Vorotex.K15.NotificationLearningLab;

internal sealed class NotificationRuleDesignerForm : Form
{
    private readonly WindowsNotificationObservation _observation;
    private readonly TextBox _ruleId = new();
    private readonly ComboBox _priority = new();
    private readonly ComboBox _behavior = new();
    private readonly ComboBox _effect = new();
    private readonly ComboBox _colorMode = new();
    private readonly TextBox _color = new();
    private readonly NumericUpDown _brightness = new();
    private readonly NumericUpDown _speed = new();
    private readonly NumericUpDown _direction = new();
    private readonly NumericUpDown _duration = new();
    private readonly NumericUpDown _maxDuration = new();
    private readonly CheckBox _includeTitle = new();
    private readonly TextBox _preview = new();
    private readonly Label _validation = new();
    private readonly Button _copy = new();

    public NotificationRuleDesignerForm(WindowsNotificationObservation observation, bool includeTitleCondition)
    {
        _observation = observation;
        Text = "VOROTEX K15 Notification Rule Designer";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 680);
        Size = new Size(980, 760);
        BackColor = Color.FromArgb(15, 17, 21);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 9F);

        BuildUi();
        LoadDefaults(includeTitleCondition);
        WireChanges();
        RefreshPreview();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(14),
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildOptions(), 0, 0);
        root.Controls.Add(BuildPreview(), 1, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _validation.AutoSize = true;
        _validation.ForeColor = Color.FromArgb(174, 220, 174);
        footer.Controls.Add(_validation, 0, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _copy.Text = "Copy TOML";
        _copy.AutoSize = true;
        StyleButton(_copy);
        _copy.Click += (_, _) => CopyPreview();
        buttons.Controls.Add(_copy);
        var close = new Button { Text = "Close", AutoSize = true };
        StyleButton(close);
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(footer, 0, 1);
        root.SetColumnSpan(footer, 2);
    }

    private Control BuildOptions()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(23, 26, 33)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        AddRow(panel, ref row, "Application", ReadOnly(_observation.AppName));
        AddRow(panel, ref row, "Rule ID", _ruleId);
        AddRow(panel, ref row, "Priority", _priority);
        AddRow(panel, ref row, "Behavior", _behavior);
        AddRow(panel, ref row, "Effect", _effect);
        AddRow(panel, ref row, "Color mode", _colorMode);

        var colorPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        colorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        colorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _color.Dock = DockStyle.Fill;
        colorPanel.Controls.Add(_color, 0, 0);
        var pick = new Button { Text = "Pick…", AutoSize = true };
        StyleButton(pick);
        pick.Click += (_, _) => PickColor();
        colorPanel.Controls.Add(pick, 1, 0);
        AddRow(panel, ref row, "Color", colorPanel);

        ConfigureNumeric(_brightness, 1, 6, 6, 0);
        ConfigureNumeric(_speed, 1, 7, 7, 0);
        ConfigureNumeric(_direction, 0, 1, 0, 0);
        ConfigureNumeric(_duration, 0.5M, 300, 6, 1);
        ConfigureNumeric(_maxDuration, 1, 3600, 60, 0);
        AddRow(panel, ref row, "Brightness", _brightness);
        AddRow(panel, ref row, "Speed", _speed);
        AddRow(panel, ref row, "Direction", _direction);
        AddRow(panel, ref row, "Pulse duration, s", _duration);
        AddRow(panel, ref row, "Max duration, s", _maxDuration);

        _includeTitle.AutoSize = true;
        _includeTitle.ForeColor = Color.FromArgb(245, 190, 100);
        _includeTitle.Text = "Persist selected title as match condition";
        panel.Controls.Add(_includeTitle, 0, row);
        panel.SetColumnSpan(_includeTitle, 2);
        row++;

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Text = "Body text is never inserted by this designer. The dialog only generates a draft and never writes notifications.toml.",
            ForeColor = Color.FromArgb(145, 158, 180),
            Margin = new Padding(4, 14, 4, 4)
        }, 0, row);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, row)!, 2);
        return panel;
    }

    private Control BuildPreview()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10),
            Margin = new Padding(10, 0, 0, 0),
            BackColor = Color.FromArgb(23, 26, 33)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "TOML preview",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        _preview.Dock = DockStyle.Fill;
        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.ScrollBars = ScrollBars.Both;
        _preview.WordWrap = false;
        _preview.Font = new Font("Consolas", 9F);
        _preview.BackColor = Color.FromArgb(15, 17, 21);
        _preview.ForeColor = ForeColor;
        panel.Controls.Add(_preview, 0, 1);
        return panel;
    }

    private void LoadDefaults(bool includeTitleCondition)
    {
        _ruleId.Text = NotificationRuleDraftBuilder.BuildRuleId(
            _observation.AppName, _observation.PackageFamilyName, _observation.AppUserModelId);

        _priority.DropDownStyle = ComboBoxStyle.DropDownList;
        _priority.DataSource = Enum.GetValues<NotificationPriority>();
        _priority.SelectedItem = NotificationPriority.Normal;

        _behavior.DropDownStyle = ComboBoxStyle.DropDownList;
        _behavior.DataSource = Enum.GetValues<NotificationBehavior>();
        _behavior.SelectedItem = NotificationBehavior.Pulse;

        _colorMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _colorMode.DataSource = Enum.GetValues<NotificationColorMode>();
        _colorMode.SelectedItem = NotificationColorMode.Custom;

        _effect.DropDownStyle = ComboBoxStyle.DropDownList;
        _effect.Items.AddRange(["constant", "flowing_water", "single_color_breathing", "cycle_breathing", "off"]);
        _effect.SelectedItem = "single_color_breathing";

        _color.Text = "#FFFFFF";
        _includeTitle.Checked = includeTitleCondition;
    }

    private void WireChanges()
    {
        _ruleId.TextChanged += (_, _) => RefreshPreview();
        _priority.SelectedIndexChanged += (_, _) => RefreshPreview();
        _behavior.SelectedIndexChanged += (_, _) => RefreshPreview();
        _effect.SelectedIndexChanged += (_, _) => RefreshPreview();
        _colorMode.SelectedIndexChanged += (_, _) => RefreshPreview();
        _color.TextChanged += (_, _) => RefreshPreview();
        _brightness.ValueChanged += (_, _) => RefreshPreview();
        _speed.ValueChanged += (_, _) => RefreshPreview();
        _direction.ValueChanged += (_, _) => RefreshPreview();
        _duration.ValueChanged += (_, _) => RefreshPreview();
        _maxDuration.ValueChanged += (_, _) => RefreshPreview();
        _includeTitle.CheckedChanged += (_, _) => RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            var options = new NotificationRuleDraftOptions
            {
                RuleId = _ruleId.Text,
                IncludeTitleCondition = _includeTitle.Checked,
                Priority = _priority.SelectedItem is NotificationPriority priority ? priority : NotificationPriority.Normal,
                Behavior = _behavior.SelectedItem is NotificationBehavior behavior ? behavior : NotificationBehavior.Pulse,
                MaxDurationSeconds = (double)_maxDuration.Value,
                Display = new NotificationVisualConfig
                {
                    Effect = _effect.SelectedItem?.ToString() ?? "single_color_breathing",
                    ColorMode = _colorMode.SelectedItem is NotificationColorMode colorMode ? colorMode : NotificationColorMode.Custom,
                    Color = _color.Text.Trim(),
                    Brightness = (int)_brightness.Value,
                    Speed = (int)_speed.Value,
                    Direction = (int)_direction.Value,
                    DurationSeconds = (double)_duration.Value
                }
            };
            _preview.Text = NotificationRuleDraftBuilder.Build(_observation, options);
            _validation.Text = "Draft valid · ready to copy";
            _validation.ForeColor = Color.FromArgb(174, 220, 174);
            _copy.Enabled = true;
        }
        catch (Exception ex)
        {
            _preview.Text = $"Cannot generate valid rule:\r\n\r\n{ex.Message}";
            _validation.Text = "Draft invalid";
            _validation.ForeColor = Color.FromArgb(255, 150, 145);
            _copy.Enabled = false;
        }
    }

    private void PickColor()
    {
        using var dialog = new ColorDialog { FullOpen = true };
        try
        {
            dialog.Color = ColorTranslator.FromHtml(_color.Text.Trim());
        }
        catch
        {
            dialog.Color = Color.White;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _color.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void CopyPreview()
    {
        if (!_copy.Enabled || string.IsNullOrWhiteSpace(_preview.Text))
            return;
        Clipboard.SetText(_preview.Text);
        _validation.Text = "Copied to clipboard · notifications.toml unchanged";
    }

    private static void ConfigureNumeric(NumericUpDown control, decimal min, decimal max, decimal value, int decimals)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Value = value;
        control.DecimalPlaces = decimals;
        control.Increment = decimals > 0 ? 0.5M : 1M;
        control.Dock = DockStyle.Fill;
    }

    private static TextBox ReadOnly(string value) => new()
    {
        Text = string.IsNullOrWhiteSpace(value) ? "(unknown)" : value,
        ReadOnly = true,
        Dock = DockStyle.Fill
    };

    private static void AddRow(TableLayoutPanel panel, ref int row, string label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Color.FromArgb(166, 178, 198),
            Margin = new Padding(4, 8, 8, 4)
        }, 0, row);
        control.Margin = new Padding(4);
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 1, row);
        row++;
    }

    private static void StyleButton(Button button)
    {
        button.BackColor = Color.FromArgb(32, 40, 56);
        button.ForeColor = Color.FromArgb(235, 240, 248);
        button.FlatStyle = FlatStyle.Flat;
        button.Margin = new Padding(4);
    }
}
