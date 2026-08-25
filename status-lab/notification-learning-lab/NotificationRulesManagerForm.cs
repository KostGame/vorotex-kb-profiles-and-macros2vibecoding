using System.Diagnostics;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.NotificationLearningLab;

internal sealed class NotificationRulesManagerForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly BindingSource _source = new();
    private List<RuleRow> _rows = [];

    public bool RulesChanged { get; private set; }

    public NotificationRulesManagerForm()
    {
        Text = "VOROTEX K15 Notification Rules Manager";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 560);
        Size = new Size(1160, 680);
        BackColor = Color.FromArgb(15, 17, 21);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 9F);

        BuildUi();
        Reload();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Notification Rules Manager\nEnable/disable and delete are explicit operations. Every real mutation creates notifications.toml.bak first.",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 10, 0, 4)
        };
        actions.Controls.Add(Button("Refresh", (_, _) => Reload()));
        actions.Controls.Add(Button("Enable / Disable selected", (_, _) => ToggleSelected()));
        actions.Controls.Add(Button("Delete selected…", (_, _) => DeleteSelected()));
        actions.Controls.Add(Button("Restore backup…", (_, _) => RestoreBackup()));
        actions.Controls.Add(Button("Open notifications.toml", (_, _) => OpenRulesFile()));
        root.Controls.Add(actions, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(170, 182, 202);
        footer.Controls.Add(_status, 0, 0);
        var close = Button("Close", (_, _) => Close());
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 3);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(20, 23, 29);
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(31, 36, 46);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(20, 23, 29);
        _grid.DefaultCellStyle.ForeColor = ForeColor;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(47, 58, 76);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.Columns.Add(Column("Enabled", nameof(RuleRow.Enabled), 72));
        _grid.Columns.Add(Column("ID", nameof(RuleRow.Id), 190));
        _grid.Columns.Add(Column("Priority", nameof(RuleRow.Priority), 82));
        _grid.Columns.Add(Column("Behavior", nameof(RuleRow.Behavior), 132));
        _grid.Columns.Add(Column("Application match", nameof(RuleRow.Identity), 260, fill: true));
        _grid.Columns.Add(Column("Effect", nameof(RuleRow.Effect), 150));
        _grid.Columns.Add(Column("Color", nameof(RuleRow.Color), 105));
        _grid.Columns.Add(Column("Duration", nameof(RuleRow.Duration), 105));
        _grid.DataSource = _source;
    }

    private void Reload(string? selectId = null)
    {
        try
        {
            var config = NotificationRulesStore.LoadFromFile(NotificationRulesConfig.FilePath);
            _rows = config.Rules.Select(RuleRow.From).ToList();
            _source.DataSource = _rows;
            _source.ResetBindings(false);
            _status.Text = $"Rules: {_rows.Count} · backup: {(File.Exists(NotificationRulesStore.BackupPath) ? "available" : "none")}";

            if (!string.IsNullOrWhiteSpace(selectId))
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.DataBoundItem is RuleRow item && string.Equals(item.Id, selectId, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        _grid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _rows = [];
            _source.DataSource = _rows;
            _source.ResetBindings(false);
            _status.Text = $"Cannot load rules: {ex.Message}";
        }
    }

    private RuleRow? Selected() => _grid.CurrentRow?.DataBoundItem as RuleRow;

    private void ToggleSelected()
    {
        var selected = Selected();
        if (selected is null)
        {
            _status.Text = "Select a rule first";
            return;
        }

        var newValue = !selected.EnabledValue;
        var answer = MessageBox.Show(this,
            $"{(newValue ? "Enable" : "Disable")} rule '{selected.Id}'?\r\n\r\nThe current notifications.toml will be backed up first.",
            "Change notification rule",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            NotificationRulesStore.SetRuleEnabled(selected.Id, newValue);
            RulesChanged = true;
            Reload(selected.Id);
            _status.Text = $"Rule '{selected.Id}' {(newValue ? "enabled" : "disabled")} · backup updated";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "notifications.toml was not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelected()
    {
        var selected = Selected();
        if (selected is null)
        {
            _status.Text = "Select a rule first";
            return;
        }

        var answer = MessageBox.Show(this,
            $"Delete rule '{selected.Id}'?\r\n\r\nThis is reversible with Restore backup. The current file will be backed up before deletion.",
            "Delete notification rule",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            NotificationRulesStore.DeleteRule(selected.Id);
            RulesChanged = true;
            Reload();
            _status.Text = $"Rule '{selected.Id}' deleted · backup updated";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "notifications.toml was not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreBackup()
    {
        if (!File.Exists(NotificationRulesStore.BackupPath))
        {
            MessageBox.Show(this, "notifications.toml.bak does not exist yet.", "Restore notification rules",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Restore notifications.toml.bak?\r\n\r\nThe current file will be preserved as notifications.toml.pre-restore.bak.",
            "Restore notification rules",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            NotificationRulesStore.RestoreBackup();
            RulesChanged = true;
            Reload();
            _status.Text = "Backup restored · previous current file preserved as .pre-restore.bak";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup was not restored", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenRulesFile()
    {
        NotificationRulesConfig.EnsureExists();
        Process.Start(new ProcessStartInfo { FileName = NotificationRulesConfig.FilePath, UseShellExecute = true });
    }

    private Button Button(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            BackColor = Color.FromArgb(32, 40, 56),
            ForeColor = ForeColor,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4)
        };
        button.Click += handler;
        return button;
    }

    private static DataGridViewTextBoxColumn Column(string header, string property, int width, bool fill = false) => new()
    {
        HeaderText = header,
        DataPropertyName = property,
        Width = width,
        MinimumWidth = width,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
    };

    private sealed class RuleRow
    {
        public required string Id { get; init; }
        public required bool EnabledValue { get; init; }
        public string Enabled => EnabledValue ? "Yes" : "No";
        public required string Priority { get; init; }
        public required string Behavior { get; init; }
        public required string Identity { get; init; }
        public required string Effect { get; init; }
        public required string Color { get; init; }
        public required string Duration { get; init; }

        public static RuleRow From(NotificationRule rule) => new()
        {
            Id = rule.Id,
            EnabledValue = rule.Enabled,
            Priority = rule.Priority.ToString(),
            Behavior = rule.Behavior.ToString(),
            Identity = DescribeIdentity(rule.Match),
            Effect = rule.Display.Effect,
            Color = $"{rule.Display.Color} · {rule.Display.ColorMode}",
            Duration = rule.Behavior == NotificationBehavior.Pulse
                ? $"{rule.Display.DurationSeconds:0.#}s"
                : $"max {rule.MaxDurationSeconds:0.#}s"
        };

        private static string DescribeIdentity(NotificationRuleMatch match)
        {
            if (!string.IsNullOrWhiteSpace(match.PackageFamilyName)) return $"PFN: {match.PackageFamilyName}";
            if (!string.IsNullOrWhiteSpace(match.AppUserModelId)) return $"AUMID: {match.AppUserModelId}";
            if (!string.IsNullOrWhiteSpace(match.AppName)) return $"App: {match.AppName}";
            return "(invalid identity)";
        }
    }
}
