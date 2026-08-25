using System.ComponentModel;
using System.Diagnostics;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.NotificationLearningLab;

internal sealed class NotificationLearningLabForm : Form
{
    private readonly WindowsNotificationPoller _poller = new(TimeSpan.FromSeconds(1));
    private readonly BindingList<LearningRow> _rows = [];
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Label _activeOverlay = new();
    private readonly Label _pendingOverlay = new();
    private readonly Label _schedulerDecision = new();
    private readonly Label _schedulerClock = new();
    private readonly TextBox _appName = ReadOnlyBox();
    private readonly TextBox _pfn = ReadOnlyBox();
    private readonly TextBox _aumid = ReadOnlyBox();
    private readonly TextBox _notificationId = ReadOnlyBox();
    private readonly TextBox _fingerprint = ReadOnlyBox();
    private readonly TextBox _title = ReadOnlyBox(multiline: true);
    private readonly TextBox _body = ReadOnlyBox(multiline: true);
    private readonly TextBox _rulePreview = ReadOnlyBox(multiline: true);
    private readonly CheckBox _includeTitle = new();
    private readonly Button _copyDraft = new();
    private readonly System.Windows.Forms.Timer _schedulerTimer = new() { Interval = 250 };

    private NotificationRulesConfig _rulesConfig = NotificationRulesConfig.CreateDefault();
    private NotificationRuleEngine _ruleEngine = new([]);
    private NotificationLearningBuffer _buffer = new(50);
    private NotificationOverlayScheduler _scheduler = new();
    private string _lastSchedulerDecision = "idle";

    public NotificationLearningLabForm()
    {
        Text = "VOROTEX K15 Notification Learning Lab";
        MinimumSize = new Size(1080, 760);
        Size = new Size(1380, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 21);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 9F);

        BuildUi();
        ReloadRules(showMessage: false);

        _poller.StatusChanged += value => Ui(() => _status.Text = value);
        _poller.NotificationChanged += observation => Ui(() => Observe(observation));

        _schedulerTimer.Tick += (_, _) =>
        {
            var decision = _scheduler.Tick(DateTimeOffset.UtcNow);
            if (decision is not null)
                _lastSchedulerDecision = decision.Reason;
            UpdateSchedulerSimulation();
        };
        _schedulerTimer.Start();

        Shown += async (_, _) =>
        {
            try
            {
                var started = await _poller.StartAsync();
                if (!started)
                {
                    MessageBox.Show(this,
                        "Windows не дал доступ к истории уведомлений. Разреши Notification access и перезапусти Learning Lab.",
                        "Notification Learning Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _status.Text = $"Ошибка listener: 0x{ex.HResult:X8}";
                MessageBox.Show(this, ex.Message, "Notification listener", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        FormClosed += async (_, _) =>
        {
            _schedulerTimer.Stop();
            await _poller.DisposeAsync();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSchedulerPanel(), 0, 1);
        root.Controls.Add(BuildGrid(), 0, 2);
        root.Controls.Add(BuildDetails(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Windows Notification Learning Lab\nЖивые toast-данные хранятся только в RAM. M3b показывает, как bounded scheduler разрулил бы overlay-очередь.",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 12, 0)
        }, 0, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(Button("Reload rules", (_, _) => ReloadRules(showMessage: true)));
        buttons.Controls.Add(Button("Open notifications.toml", (_, _) => OpenRulesFile()));
        buttons.Controls.Add(Button("Clear RAM", (_, _) => ClearLearningBuffer()));
        panel.Controls.Add(buttons, 1, 0);
        return panel;
    }

    private Control BuildSchedulerPanel()
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Color.FromArgb(23, 26, 33)
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));

        outer.Controls.Add(SchedulerCell("ACTIVE", _activeOverlay, Color.FromArgb(150, 210, 255)), 0, 0);
        outer.Controls.Add(SchedulerCell("PENDING", _pendingOverlay, Color.FromArgb(245, 190, 100)), 1, 0);
        outer.Controls.Add(SchedulerCell("LAST DECISION", _schedulerDecision, Color.FromArgb(174, 220, 174)), 2, 0);
        outer.Controls.Add(SchedulerCell("CLOCK", _schedulerClock, Color.FromArgb(160, 170, 190)), 3, 0);

        var help = new Label
        {
            AutoSize = true,
            Text = "Simulation only: one ACTIVE + max one PENDING. Higher priority preempts; expiry/removal/ack may promote pending.",
            ForeColor = Color.FromArgb(150, 160, 180),
            Margin = new Padding(4, 7, 8, 0)
        };
        outer.Controls.Add(help, 0, 1);
        outer.SetColumnSpan(help, 2);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(Button("Acknowledge ACTIVE", (_, _) => AcknowledgeActive()));
        actions.Controls.Add(Button("Clear scheduler", (_, _) => ClearScheduler()));
        outer.Controls.Add(actions, 2, 1);
        outer.SetColumnSpan(actions, 2);
        return outer;
    }

    private static Control SchedulerCell(string caption, Label value, Color color)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(4)
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = caption,
            ForeColor = Color.FromArgb(130, 142, 165),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold)
        }, 0, 0);
        value.AutoSize = true;
        value.Text = "—";
        value.ForeColor = color;
        value.Font = new Font("Consolas", 9F, FontStyle.Bold);
        panel.Controls.Add(value, 0, 1);
        return panel;
    }

    private Control BuildGrid()
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
        _grid.DataSource = _rows;
        _grid.Columns.Add(Column("Time", nameof(LearningRow.Time), 82));
        _grid.Columns.Add(Column("Event", nameof(LearningRow.Change), 78));
        _grid.Columns.Add(Column("App", nameof(LearningRow.App), 150));
        _grid.Columns.Add(Column("Title", nameof(LearningRow.Title), 260, fill: true));
        _grid.Columns.Add(Column("Rule", nameof(LearningRow.Rule), 150));
        _grid.Columns.Add(Column("Intent", nameof(LearningRow.Intent), 170));
        _grid.SelectionChanged += (_, _) => ShowSelected();
        return _grid;
    }

    private Control BuildDetails()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 680,
            BackColor = BackColor,
            Margin = new Padding(0, 10, 0, 10)
        };

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(23, 26, 33)
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        AddField(details, "Application", _appName, 0, 0);
        AddField(details, "Notification ID", _notificationId, 1, 0);
        AddField(details, "Package Family Name", _pfn, 0, 1, span: 2);
        AddField(details, "AppUserModelId", _aumid, 0, 2, span: 2);
        AddField(details, "Fingerprint", _fingerprint, 0, 3, span: 2);
        AddField(details, "Title · RAM only", _title, 0, 4, span: 2, fill: true);
        AddField(details, "Body · RAM only", _body, 0, 5, span: 2, fill: true);
        split.Panel1.Controls.Add(details);

        var draft = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(23, 26, 33)
        };
        draft.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        draft.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        draft.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        draft.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        draft.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Rule draft",
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ForeColor
        }, 0, 0);
        _includeTitle.AutoSize = true;
        _includeTitle.Text = "Include selected title in persistent match rule";
        _includeTitle.ForeColor = Color.FromArgb(245, 190, 100);
        _includeTitle.CheckedChanged += (_, _) => UpdateRulePreview();
        draft.Controls.Add(_includeTitle, 0, 1);
        _rulePreview.Dock = DockStyle.Fill;
        _rulePreview.Font = new Font("Consolas", 9F);
        _rulePreview.ScrollBars = ScrollBars.Both;
        draft.Controls.Add(_rulePreview, 0, 2);

        var draftActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _copyDraft.Text = "Quick copy draft";
        _copyDraft.AutoSize = true;
        _copyDraft.Enabled = false;
        _copyDraft.Click += (_, _) => CopyRuleDraft();
        draftActions.Controls.Add(_copyDraft);
        draftActions.Controls.Add(Button("Design rule…", (_, _) => OpenRuleDesigner()));
        draft.Controls.Add(draftActions, 0, 3);

        split.Panel2.Controls.Add(draft);
        return split;
    }

    private Control BuildFooter()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.AutoSize = true;
        _status.Text = "Уведомления: запуск...";
        _status.ForeColor = Color.FromArgb(170, 182, 202);
        panel.Controls.Add(_status, 0, 0);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "M4a observation/design-only · no keyboard rendering",
            ForeColor = Color.FromArgb(120, 132, 150)
        }, 1, 0);
        return panel;
    }

    private void Observe(WindowsNotificationObservation observation)
    {
        _buffer.Observe(observation);
        var intent = _ruleEngine.Evaluate(observation);
        if (intent is not null)
        {
            var decision = _scheduler.Apply(intent, DateTimeOffset.UtcNow);
            if (decision is not null)
                _lastSchedulerDecision = decision.Reason;
            else if (_scheduler.Pending is not null &&
                     string.Equals(_scheduler.Pending.Intent.NotificationKey, intent.NotificationKey, StringComparison.Ordinal))
                _lastSchedulerDecision = "queued_or_coalesced_pending";
        }

        RefreshRows(observation.Key);
        UpdateSchedulerSimulation();
    }

    private void RefreshRows(string? selectKey = null)
    {
        var snapshot = _buffer.Snapshot();
        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        foreach (var observation in snapshot)
        {
            var matched = _rulesConfig.Rules
                .FirstOrDefault(rule => rule.Enabled && NotificationRuleEngine.Matches(rule.Match, observation));
            var intent = _ruleEngine.Evaluate(observation);
            _rows.Add(new LearningRow(observation, matched?.Id ?? "—", DescribeIntent(intent, observation.ChangeKind)));
        }
        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();

        if (selectKey is not null)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.DataBoundItem is LearningRow learning && learning.Observation.Key == selectKey)
                {
                    row.Selected = true;
                    _grid.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }
        ShowSelected();
    }

    private void UpdateSchedulerSimulation()
    {
        var now = DateTimeOffset.UtcNow;
        _activeOverlay.Text = DescribeScheduled(_scheduler.Active, now);
        _pendingOverlay.Text = DescribeScheduled(_scheduler.Pending, now);
        _schedulerDecision.Text = _lastSchedulerDecision;
        _schedulerClock.Text = DateTimeOffset.Now.ToString("HH:mm:ss");
    }

    private static string DescribeScheduled(ScheduledNotificationOverlay? overlay, DateTimeOffset nowUtc)
    {
        if (overlay is null)
            return "—";

        var left = overlay.ExpiresUtc - nowUtc;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        return $"{overlay.Intent.RuleId} · {overlay.Intent.Priority} · {overlay.Intent.Behavior} · {left.TotalSeconds:0.0}s";
    }

    private void AcknowledgeActive()
    {
        var active = _scheduler.Active;
        if (active is null)
        {
            _lastSchedulerDecision = "ack_ignored_no_active";
            UpdateSchedulerSimulation();
            return;
        }

        var decision = _scheduler.Acknowledge(active.Intent.NotificationKey, DateTimeOffset.UtcNow);
        _lastSchedulerDecision = decision?.Reason ?? "ack_no_change";
        UpdateSchedulerSimulation();
    }

    private void ClearScheduler()
    {
        var decision = _scheduler.Clear(DateTimeOffset.UtcNow, "learning_lab_clear");
        _lastSchedulerDecision = decision?.Reason ?? "learning_lab_clear_no_active";
        UpdateSchedulerSimulation();
    }

    private void ShowSelected()
    {
        var selected = SelectedObservation();
        _appName.Text = selected?.AppName ?? string.Empty;
        _pfn.Text = selected?.PackageFamilyName ?? string.Empty;
        _aumid.Text = selected?.AppUserModelId ?? string.Empty;
        _notificationId.Text = selected?.NotificationId.ToString() ?? string.Empty;
        _fingerprint.Text = selected?.TextFingerprint ?? string.Empty;
        _title.Text = selected?.Title ?? string.Empty;
        _body.Text = selected?.Body ?? string.Empty;
        _copyDraft.Enabled = selected is not null && HasIdentity(selected);
        UpdateRulePreview();
    }

    private void UpdateRulePreview()
    {
        var selected = SelectedObservation();
        if (selected is null || !HasIdentity(selected))
        {
            _rulePreview.Text = "Select a notification with PFN, AUMID or application name.";
            return;
        }

        try
        {
            _rulePreview.Text = NotificationRuleDraftBuilder.Build(selected, _includeTitle.Checked);
        }
        catch (Exception ex)
        {
            _rulePreview.Text = $"Cannot create safe draft: {ex.Message}";
        }
    }

    private void CopyRuleDraft()
    {
        if (string.IsNullOrWhiteSpace(_rulePreview.Text))
            return;
        Clipboard.SetText(_rulePreview.Text);
        _status.Text = _includeTitle.Checked
            ? "Rule draft copied · title condition included"
            : "Rule draft copied · application identity only";
    }

    private void OpenRuleDesigner()
    {
        var selected = SelectedObservation();
        if (selected is null || !HasIdentity(selected))
        {
            _status.Text = "Select a notification with a stable application identity first";
            return;
        }

        using var designer = new NotificationRuleDesignerForm(selected, _includeTitle.Checked);
        designer.ShowDialog(this);
    }

    private void ReloadRules(bool showMessage)
    {
        _rulesConfig = NotificationRulesConfig.LoadOrCreate();
        _ruleEngine = new NotificationRuleEngine(_rulesConfig.Enabled ? _rulesConfig.Rules : []);
        _buffer = new NotificationLearningBuffer(_rulesConfig.LearningBufferSize);
        _scheduler = new NotificationOverlayScheduler();
        _lastSchedulerDecision = "rules_reloaded";
        _rows.Clear();
        ClearDetails();
        UpdateSchedulerSimulation();

        var message = _rulesConfig.LoadWarning ??
            $"Rules loaded: {_rulesConfig.Rules.Count} · learning buffer: {_rulesConfig.LearningBufferSize}";
        _status.Text = message;
        if (showMessage && !string.IsNullOrWhiteSpace(_rulesConfig.LoadWarning))
            MessageBox.Show(this, _rulesConfig.LoadWarning, "notifications.toml", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ClearLearningBuffer()
    {
        _buffer.Clear();
        _scheduler = new NotificationOverlayScheduler();
        _lastSchedulerDecision = "ram_cleared";
        _rows.Clear();
        ClearDetails();
        UpdateSchedulerSimulation();
        _status.Text = "Learning RAM buffer + scheduler simulation cleared";
    }

    private void ClearDetails()
    {
        _appName.Clear();
        _pfn.Clear();
        _aumid.Clear();
        _notificationId.Clear();
        _fingerprint.Clear();
        _title.Clear();
        _body.Clear();
        _rulePreview.Clear();
        _copyDraft.Enabled = false;
    }

    private static void OpenRulesFile()
    {
        NotificationRulesConfig.EnsureExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = NotificationRulesConfig.FilePath,
            UseShellExecute = true
        });
    }

    private WindowsNotificationObservation? SelectedObservation() =>
        _grid.CurrentRow?.DataBoundItem is LearningRow row ? row.Observation : null;

    private static bool HasIdentity(WindowsNotificationObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.PackageFamilyName) ||
        !string.IsNullOrWhiteSpace(observation.AppUserModelId) ||
        !string.IsNullOrWhiteSpace(observation.AppName);

    private static string DescribeIntent(NotificationOverlayIntent? intent, WindowsNotificationChangeKind changeKind)
    {
        if (intent is not null)
            return intent.Dismiss ? $"dismiss · {intent.Behavior}" : $"overlay · {intent.Behavior}";
        return changeKind == WindowsNotificationChangeKind.Present ? "startup · none" : "none";
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
            return;
        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static DataGridViewTextBoxColumn Column(string header, string property, int width, bool fill = false) => new()
    {
        HeaderText = header,
        DataPropertyName = property,
        Width = width,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        MinimumWidth = width
    };

    private static TextBox ReadOnlyBox(bool multiline = false) => new()
    {
        ReadOnly = true,
        Multiline = multiline,
        BackColor = Color.FromArgb(15, 17, 21),
        ForeColor = Color.FromArgb(235, 240, 248),
        BorderStyle = BorderStyle.FixedSingle,
        ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
    };

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

    private static void AddField(TableLayoutPanel panel, string label, Control control, int column, int row,
        int span = 1, bool fill = false)
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(5)
        };
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        container.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = Color.FromArgb(166, 178, 198),
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 0);
        control.Dock = fill ? DockStyle.Fill : DockStyle.Top;
        if (!fill)
            control.Height = 25;
        container.Controls.Add(control, 0, 1);
        panel.Controls.Add(container, column, row);
        if (span > 1)
            panel.SetColumnSpan(container, span);
    }

    private sealed class LearningRow
    {
        public LearningRow(WindowsNotificationObservation observation, string rule, string intent)
        {
            Observation = observation;
            Rule = rule;
            Intent = intent;
        }

        public WindowsNotificationObservation Observation { get; }
        public string Time => Observation.CreationTime.ToLocalTime().ToString("HH:mm:ss");
        public string Change => Observation.ChangeKind.ToString();
        public string App => string.IsNullOrWhiteSpace(Observation.AppName) ? "(unknown)" : Observation.AppName;
        public string Title => Observation.Title;
        public string Rule { get; }
        public string Intent { get; }
    }
}
