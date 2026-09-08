using System.Drawing;
using System.Windows.Forms;
using Vorotex.K15.Clients;

namespace Vorotex.K15.StatusTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon = new() { Icon = SystemIcons.Application, Visible = true, Text = "VOROTEX K15 Runtime" };
    private readonly RuntimeClientLoop _runtime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private RuntimeClientProjection _projection = RuntimeClientProjection.Offline();
    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Control Center", null, (_, _) => Launch("Vorotex.K15.ControlCenter.exe"));
        menu.Items.Add("Live Dashboard", null, (_, _) => Launch("Vorotex.K15.LiveDashboard.exe"));
        menu.Items.Add("RGB on/off", null, async (_, _) => await ToggleRgbAsync());
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon.ContextMenuStrip = menu; _icon.DoubleClick += (_, _) => Launch("Vorotex.K15.ControlCenter.exe");
        _timer.Tick += async (_, _) => await RefreshAsync(); _timer.Start(); _ = RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        _projection = await _runtime.ReadAsync();
        var health = _projection.Degraded ? $"DEGRADED {_projection.NativeAuthorityHealth.Detail}" : "READY";
        var text = $"K15 {_projection.State} · W {_projection.WaitingCount} · {health}";
        _icon.Text = text[..Math.Min(63, text.Length)];
    }
    private async Task ToggleRgbAsync() => await _runtime.Client.SendCommandAsync("set_rgb_enabled", enabled: !_projection.Rgb.Enabled);
    private static void Launch(string name) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name) { UseShellExecute = true }); } catch { } }
    protected override void ExitThreadCore() { _timer.Stop(); _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); base.ExitThreadCore(); }
}
