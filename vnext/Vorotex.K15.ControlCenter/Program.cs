using System.Text;
using System.Windows.Forms;
using Vorotex.K15.Clients;

namespace Vorotex.K15.ControlCenter;

internal static class Program { [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new ControlCenterForm()); } }

internal sealed class ControlCenterForm : Form
{
    private readonly RuntimeClientLoop _runtime = new(); private readonly TextBox _view = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both }; private readonly CheckBox _diagnostics = new() { Text = "Show service/canary diagnostics" }; private readonly Label _status = new() { Dock = DockStyle.Top, Height = 28 };
    public ControlCenterForm() { Text = "VOROTEX K15 Control Center (Runtime)"; Width = 900; Height = 600; var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 }; foreach (var item in new[] { ("Refresh", "snapshot"), ("Scan devices", "scan_devices"), ("Disconnect", "disconnect_device"), ("Reconnect", "reconnect_device"), ("RGB on", "set_rgb_enabled") }) { var button = new Button { Text = item.Item1, AutoSize = true }; button.Click += async (_, _) => await CommandAsync(item.Item2); bar.Controls.Add(button); } _diagnostics.CheckedChanged += async (_, _) => await RefreshAsync(); bar.Controls.Add(_diagnostics); Controls.Add(_view); Controls.Add(_status); Controls.Add(bar); Shown += async (_, _) => await RefreshAsync(); }
    private async Task CommandAsync(string command) { var result = command == "set_rgb_enabled" ? await _runtime.Client.SendCommandAsync(command, enabled: true) : await _runtime.Client.SendCommandAsync(command); _status.Text = result.Success ? "Runtime command accepted" : $"Runtime unavailable: {result.Error}"; await RefreshAsync(); }
    private async Task RefreshAsync() { var p = await _runtime.ReadAsync(_diagnostics.Checked); _status.Text = $"Runtime: {p.Health} · {p.State} · active {p.ActiveCount} · running {p.RunningCount} · waiting {p.WaitingCount} · done {p.DoneCount}"; var text = new StringBuilder().AppendLine($"Device: {p.Device.ConnectionState} · candidates {p.Device.CandidateCount} · RGB {p.Rgb.Enabled}"); foreach (var t in p.Threads) text.AppendLine($"{t.State,-24} {t.ThreadId} · {t.Classification} · {t.WorkingDirectory ?? "no directory"} · {t.LastObservedUtc?.ToString("O") ?? "no timestamp"}"); _view.Text = text.ToString(); }
}
