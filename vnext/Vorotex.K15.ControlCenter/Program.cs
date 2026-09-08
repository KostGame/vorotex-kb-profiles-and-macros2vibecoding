using System.Text;
using System.Windows.Forms;
using Vorotex.K15.Clients;

namespace Vorotex.K15.ControlCenter;

internal static class Program { [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new ControlCenterForm()); } }

internal sealed class ControlCenterForm : Form
{
    private readonly RuntimeClientLoop _runtime = new();
    private readonly TextBox _view = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
    private readonly ComboBox _candidates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _diagnostics = new() { Text = "Show service/canary diagnostics" };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 42 };
    private readonly Button _rgb = new() { Text = "RGB" };
    private bool _lastRgbEnabled;

    public ControlCenterForm()
    {
        Text = "VOROTEX K15 Control Center (Runtime)"; Width = 1000; Height = 650;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, WrapContents = true };
        AddButton(bar, "Refresh", () => RefreshAsync()); AddButton(bar, "Scan devices", () => CommandAsync("scan_devices"));
        AddButton(bar, "Connect selected", () => CommandAsync("connect_device", SelectedCandidateId()));
        AddButton(bar, "Disconnect", () => CommandAsync("disconnect_device")); AddButton(bar, "Reconnect", () => CommandAsync("reconnect_device"));
        _rgb.Click += async (_, _) => await CommandAsync("set_rgb_enabled", enabled: !_lastRgbEnabled); bar.Controls.Add(_rgb);
        AddButton(bar, "Restore lighting", () => CommandAsync("restore_lighting")); bar.Controls.Add(_candidates); bar.Controls.Add(_diagnostics);
        Controls.Add(_view); Controls.Add(_status); Controls.Add(bar); Shown += async (_, _) => await RefreshAsync();
    }

    private string? SelectedCandidateId() => _candidates.SelectedItem is CandidateItem item ? item.Id : null;
    private void AddButton(Control parent, string text, Func<Task> action) { var button = new Button { Text = text, AutoSize = true }; button.Click += async (_, _) => await action(); parent.Controls.Add(button); }
    private async Task CommandAsync(string command, string? candidateId = null, bool? enabled = null)
    { var result = await _runtime.Client.SendCommandAsync(command, candidateId, enabled); _status.Text = result.Success ? $"Runtime accepted {command}" : $"Runtime rejected {command}: {result.Error}"; await RefreshAsync(); }
    private async Task RefreshAsync()
    {
        var p = await _runtime.ReadAsync(_diagnostics.Checked); _lastRgbEnabled = p.Rgb.Enabled;
        _status.Text = $"Runtime: {p.RuntimeHealth.Detail} ({p.RuntimeHealth.Version}) · Native authority: {p.NativeAuthorityHealth.Detail} ({p.NativeAuthorityHealth.Version}) · {p.State} · RGB {(p.Rgb.Enabled ? "ON" : "OFF")}"; _rgb.Text = p.Rgb.Enabled ? "RGB off" : "RGB on";
        var selected = SelectedCandidateId(); _candidates.Items.Clear(); foreach (var c in p.Device.Candidates) _candidates.Items.Add(new CandidateItem(c.CandidateId, $"{c.Product} [{c.CandidateId}]"));
        if (selected is not null) for (var i = 0; i < _candidates.Items.Count; i++) if (_candidates.Items[i] is CandidateItem candidate && candidate.Id == selected) _candidates.SelectedIndex = i;
        var text = new StringBuilder().AppendLine($"Device: {p.Device.ConnectionState} · candidates {p.Device.CandidateCount} · selected {p.Device.SelectedCandidateId ?? "none"}").AppendLine($"RGB: {(p.Rgb.Enabled ? "ON" : "OFF")} · transport {p.Rgb.TransportAvailable} · restore {p.Rgb.RestoreSnapshotAvailable}");
        foreach (var t in p.Threads) text.AppendLine($"{t.State,-24} {t.ThreadId} · {t.Classification} · {t.WorkingDirectory ?? "no directory"} · {t.LastObservedUtc?.ToString("O") ?? "no timestamp"}"); _view.Text = text.ToString();
    }
    private sealed record CandidateItem(string Id, string Display) { public override string ToString() => Display; }
}
