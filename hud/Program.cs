using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Vorotex.K15.Hud;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new HudApplicationContext());
    }
}

internal sealed class HudApplicationContext : ApplicationContext
{
    private const int HotkeyToggle = 1;
    private const int HotkeyNextProfile = 2;
    private const int HotkeyBothProfiles = 3;

    private readonly HudConfig _config;
    private readonly OverlayForm _overlay;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly NotifyIcon _trayIcon;
    private string _currentProfileId;

    public HudApplicationContext()
    {
        _config = HudConfig.Load();
        _currentProfileId = _config.Profiles.Any(p => p.Id == _config.DefaultProfile)
            ? _config.DefaultProfile
            : _config.Profiles.First().Id;

        _overlay = new OverlayForm(_config.AutoHideMs);
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += HandleHotkey;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VOROTEX K15 HUD",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleCurrentProfile();

        RegisterHotkeys();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать / скрыть\tF13", null, (_, _) => ToggleCurrentProfile());
        menu.Items.Add("Профиль A\tShift+F13", null, (_, _) => ShowProfile("A"));
        menu.Items.Add("Профиль B", null, (_, _) => ShowProfile("B"));
        menu.Items.Add("Оба профиля\tCtrl+F13", null, (_, _) => ShowBoth());
        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Запускать с Windows")
        {
            Checked = AutoStartManager.IsEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) =>
        {
            try
            {
                AutoStartManager.SetEnabled(startupItem.Checked);
            }
            catch (Exception ex)
            {
                startupItem.Checked = AutoStartManager.IsEnabled();
                ShowTrayError($"Не удалось изменить автозапуск: {ex.Message}");
            }
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        return menu;
    }

    private void RegisterHotkeys()
    {
        var failures = new List<string>();
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, HotkeyToggle, NativeMethods.ModNoRepeat, (uint)Keys.F13))
            failures.Add("F13");
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, HotkeyNextProfile, NativeMethods.ModShift | NativeMethods.ModNoRepeat, (uint)Keys.F13))
            failures.Add("Shift+F13");
        if (!NativeMethods.RegisterHotKey(_hotkeyWindow.Handle, HotkeyBothProfiles, NativeMethods.ModControl | NativeMethods.ModNoRepeat, (uint)Keys.F13))
            failures.Add("Ctrl+F13");

        if (failures.Count > 0)
            ShowTrayError("Не удалось зарегистрировать хоткей: " + string.Join(", ", failures));
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case HotkeyToggle:
                ToggleCurrentProfile();
                break;
            case HotkeyNextProfile:
                CycleProfile();
                break;
            case HotkeyBothProfiles:
                ShowBoth();
                break;
        }
    }

    private void ToggleCurrentProfile()
    {
        if (_overlay.Visible)
        {
            _overlay.HideOverlay();
            return;
        }

        ShowProfile(_currentProfileId);
    }

    private void CycleProfile()
    {
        var index = _config.Profiles.FindIndex(p => p.Id == _currentProfileId);
        if (index < 0)
            index = 0;
        _currentProfileId = _config.Profiles[(index + 1) % _config.Profiles.Count].Id;
        ShowProfile(_currentProfileId);
    }

    private void ShowProfile(string id)
    {
        var profile = _config.Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return;
        _currentProfileId = profile.Id;
        _overlay.ShowProfiles([profile], Cursor.Position);
    }

    private void ShowBoth()
    {
        _overlay.ShowProfiles(_config.Profiles, Cursor.Position);
    }

    private void ShowTrayError(string message)
    {
        _trayIcon.BalloonTipTitle = "VOROTEX K15 HUD";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _trayIcon.ShowBalloonTip(5000);
    }

    protected override void ExitThreadCore()
    {
        NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyToggle);
        NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyNextProfile);
        NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyBothProfiles);
        _hotkeyWindow.Dispose();
        _overlay.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action<int>? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "VorotexK15HudHotkeyWindow",
            X = -32000,
            Y = -32000,
            Width = 1,
            Height = 1
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
        GC.SuppressFinalize(this);
    }
}

internal sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer _autoHideTimer;
    private IReadOnlyList<ProfileDefinition> _profiles = Array.Empty<ProfileDefinition>();

    private static readonly Color BackgroundColor = Color.FromArgb(17, 21, 25);
    private static readonly Color PanelColor = Color.FromArgb(25, 32, 36);
    private static readonly Color KeyColor = Color.FromArgb(22, 97, 101);
    private static readonly Color PrimaryColor = Color.FromArgb(21, 126, 130);
    private static readonly Color FlowColor = Color.FromArgb(74, 112, 47);
    private static readonly Color SendColor = Color.FromArgb(194, 137, 22);
    private static readonly Color TealColor = Color.FromArgb(55, 218, 210);
    private static readonly Color MutedTextColor = Color.FromArgb(169, 184, 188);

    public OverlayForm(int autoHideMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BackgroundColor;
        Opacity = 0.97;
        DoubleBuffered = true;
        MinimumSize = new Size(520, 360);

        _autoHideTimer = new System.Windows.Forms.Timer { Interval = Math.Max(0, autoHideMs) };
        _autoHideTimer.Tick += (_, _) => HideOverlay();
        MouseDown += (_, _) => HideOverlay();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
            return cp;
        }
    }

    public void ShowProfiles(IReadOnlyList<ProfileDefinition> profiles, Point cursor)
    {
        _profiles = profiles;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        var logicalWidth = profiles.Count > 1 ? 980 : 560;
        var logicalHeight = 390;
        ClientSize = new Size((int)(logicalWidth * scale), (int)(logicalHeight * scale));
        ApplyRoundedRegion((int)(18 * scale));
        PositionNearCursor(cursor, (int)(16 * scale));
        Invalidate();

        if (!Visible)
            Show();
        else
            NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, Left, Top, Width, Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

        _autoHideTimer.Stop();
        if (_autoHideTimer.Interval > 0)
            _autoHideTimer.Start();
    }

    public void HideOverlay()
    {
        _autoHideTimer.Stop();
        Hide();
    }

    private void PositionNearCursor(Point cursor, int gap)
    {
        var work = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X + gap;
        var y = cursor.Y + gap;

        if (x + Width > work.Right)
            x = cursor.X - Width - gap;
        if (y + Height > work.Bottom)
            y = cursor.Y - Height - gap;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - Height));
        Location = new Point(x, y);
    }

    private void ApplyRoundedRegion(int radius)
    {
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), radius);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var backgroundBrush = new SolidBrush(BackgroundColor);
        g.FillRectangle(backgroundBrush, ClientRectangle);

        var scale = Math.Max(1f, DeviceDpi / 96f);
        var outer = RectangleF.Inflate(ClientRectangle, -1 * scale, -1 * scale);
        using var borderPen = new Pen(Color.FromArgb(120, TealColor), 1.2f * scale);
        using var outerPath = RoundedRect(Rectangle.Round(outer), (int)(18 * scale));
        g.DrawPath(borderPen, outerPath);

        using var titleFont = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.WhiteSmoke);
        g.DrawString("VOROTEX K15 HUD", titleFont, titleBrush, 18 * scale, 13 * scale);

        using var hintFont = new Font("Segoe UI", 8.2f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var hintBrush = new SolidBrush(MutedTextColor);
        var hint = "F13 показать/скрыть   Shift+F13 профиль   Ctrl+F13 оба";
        var hintSize = g.MeasureString(hint, hintFont);
        g.DrawString(hint, hintFont, hintBrush, Width - hintSize.Width - 18 * scale, 16 * scale);

        var contentTop = 42 * scale;
        var margin = 16 * scale;
        var gap = 12 * scale;
        var panelWidth = _profiles.Count > 1
            ? (Width - (2 * margin) - gap) / 2f
            : Width - (2 * margin);
        var panelHeight = Height - contentTop - margin;

        for (var i = 0; i < _profiles.Count; i++)
        {
            var x = margin + i * (panelWidth + gap);
            DrawProfile(g, _profiles[i], new RectangleF(x, contentTop, panelWidth, panelHeight), scale);
        }
    }

    private static void DrawProfile(Graphics g, ProfileDefinition profile, RectangleF panel, float scale)
    {
        using var panelBrush = new SolidBrush(PanelColor);
        using var panelPen = new Pen(Color.FromArgb(95, TealColor), Math.Max(1f, scale));
        using var panelPath = RoundedRect(Rectangle.Round(panel), (int)(14 * scale));
        g.FillPath(panelBrush, panelPath);
        g.DrawPath(panelPen, panelPath);

        using var profileFont = new Font("Segoe UI Semibold", 10.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var profileBrush = new SolidBrush(TealColor);
        g.DrawString($"ПРОФИЛЬ {profile.Id}  ·  {profile.Title}", profileFont, profileBrush,
            panel.Left + 12 * scale, panel.Top + 10 * scale);

        var gridTop = panel.Top + 36 * scale;
        var innerLeft = panel.Left + 12 * scale;
        var innerWidth = panel.Width - 24 * scale;
        var colGap = 5 * scale;
        var rowGap = 6 * scale;
        var keyHeight = 69 * scale;
        var colWidth = (innerWidth - (5 * colGap)) / 6f;

        var row1 = new[] { "1", "2", "3", "4", "5", "6" };
        var row2 = new[] { "7", "8", "9", "0", ".", "Enter" };

        for (var i = 0; i < 6; i++)
        {
            var rect1 = new RectangleF(innerLeft + i * (colWidth + colGap), gridTop, colWidth, keyHeight);
            DrawKey(g, row1[i], profile.GetKey(row1[i]), rect1, scale);

            var rect2 = new RectangleF(innerLeft + i * (colWidth + colGap), gridTop + keyHeight + rowGap, colWidth, keyHeight);
            DrawKey(g, row2[i], profile.GetKey(row2[i]), rect2, scale);
        }

        var bottomTop = gridTop + (2 * keyHeight) + (2 * rowGap);
        var bottomHeight = Math.Max(74 * scale, panel.Bottom - bottomTop - 11 * scale);
        var unit = (innerWidth - (3 * colGap)) / 5.6f;
        var widths = new[] { unit, 1.2f * unit, 2.0f * unit, 1.4f * unit };
        var keys = new[] { "-", "+", "Space", "Joystick" };

        var x = innerLeft;
        for (var i = 0; i < keys.Length; i++)
        {
            var width = i == keys.Length - 1 ? innerLeft + innerWidth - x : widths[i];
            DrawKey(g, keys[i], profile.GetKey(keys[i]), new RectangleF(x, bottomTop, width, bottomHeight), scale);
            x += width + colGap;
        }
    }

    private static void DrawKey(Graphics g, string key, HudKeyDefinition definition, RectangleF rect, float scale)
    {
        var fill = definition.Accent?.ToLowerInvariant() switch
        {
            "primary" => PrimaryColor,
            "flow" => FlowColor,
            "send" => SendColor,
            _ => KeyColor
        };

        using var shadowBrush = new SolidBrush(Color.FromArgb(75, 0, 0, 0));
        using var shadowPath = RoundedRect(Rectangle.Round(new RectangleF(rect.X + 2 * scale, rect.Y + 3 * scale, rect.Width, rect.Height)), (int)(8 * scale));
        g.FillPath(shadowBrush, shadowPath);

        using var keyBrush = new SolidBrush(fill);
        using var keyPen = new Pen(Color.FromArgb(150, TealColor), Math.Max(1f, scale));
        using var keyPath = RoundedRect(Rectangle.Round(rect), (int)(8 * scale));
        g.FillPath(keyBrush, keyPath);
        g.DrawPath(keyPen, keyPath);

        using var keyFont = new Font("Segoe UI Semibold", 9f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var actionFont = new Font("Segoe UI Semibold", ActionFontSize(definition.Action) * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.FromArgb(235, 245, 245, 245));
        using var actionBrush = new SolidBrush(Color.White);

        g.DrawString(DisplayKey(key), keyFont, labelBrush, rect.Left + 6 * scale, rect.Top + 5 * scale);

        var actionRect = new RectangleF(rect.Left + 4 * scale, rect.Top + 24 * scale, rect.Width - 8 * scale, rect.Height - 27 * scale);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord
        };
        g.DrawString(definition.Action, actionFont, actionBrush, actionRect, format);
    }

    private static float ActionFontSize(string action)
    {
        var length = action.Replace("\n", string.Empty).Length;
        return length switch
        {
            > 24 => 6.7f,
            > 17 => 7.2f,
            > 12 => 7.8f,
            _ => 8.4f
        };
    }

    private static string DisplayKey(string key) => key switch
    {
        "Enter" => "ENTER",
        "Space" => "SPACE",
        "Joystick" => "●",
        _ => key
    };

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        radius = Math.Max(2, radius);
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class HudConfig
{
    public int AutoHideMs { get; set; } = 9000;
    public string DefaultProfile { get; set; } = "B";
    public List<ProfileDefinition> Profiles { get; set; } = [];

    public static HudConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles.json");
        if (File.Exists(path))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<HudConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (parsed is { Profiles.Count: > 0 })
                    return parsed;
            }
            catch
            {
                // Fall back to the built-in accepted V1 map.
            }
        }
        return CreateDefault();
    }

    private static HudConfig CreateDefault() => new()
    {
        AutoHideMs = 9000,
        DefaultProfile = "B",
        Profiles =
        [
            ProfileDefinition.Create("A", "TOOLS / AUTH", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("COPY"), ["2"] = new("PASTE +\nNEW LINE"), ["3"] = new("CUT"),
                ["4"] = new("UNDO"), ["5"] = new("REDO"), ["6"] = new("SELECT ALL"),
                ["7"] = new("ОТЧЕТ"), ["8"] = new("ВОТ ОТЧЕТ"), ["9"] = new("```"),
                ["0"] = new("ОТЧЕТ ИЗ\nБУФЕРА"), ["."] = new("ДАЙ СТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА", "flow"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ОТЧЕТ ДЛЯ\nСЛЕД. ЧАТА"), ["Space"] = new("ПОДТВЕРЖДАЮ", "primary"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send")
            }),
            ProfileDefinition.Create("B", "MAIN / VIBECODING", new Dictionary<string, HudKeyDefinition>
            {
                ["1"] = new("ПРОВЕРЬ"), ["2"] = new("СЛЕДУЮЩИЙ\nШАГ"), ["3"] = new("СЛЕД. ПРОМПТ"),
                ["4"] = new("ИСПРАВЛЯЙ"), ["5"] = new("ПУБЛИКУЙ"), ["6"] = new("МЕРЖИ"),
                ["7"] = new("СОЗДАВАЙ"), ["8"] = new("ПРОДОЛЖАЙ"), ["9"] = new("РЕВЬЮ"),
                ["0"] = new("ГОТОВО"), ["."] = new("ДАЙ СТАТУС"),
                ["Enter"] = new("НОВАЯ СТРОКА", "flow"), ["-"] = new("СТОП", "flow"),
                ["+"] = new("ОТЧЕТ ДЛЯ\nСЛЕД. ЧАТА"), ["Space"] = new("ДАВАЙ ДАЛЬШЕ\nБЕЗ PUSH/MERGE", "primary"),
                ["Joystick"] = new("ОТПРАВИТЬ", "send")
            })
        ]
    };
}

internal sealed class ProfileDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Dictionary<string, HudKeyDefinition> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HudKeyDefinition GetKey(string key) => Keys.TryGetValue(key, out var definition)
        ? definition
        : new HudKeyDefinition("—");

    public static ProfileDefinition Create(string id, string title, Dictionary<string, HudKeyDefinition> keys) => new()
    {
        Id = id,
        Title = title,
        Keys = new Dictionary<string, HudKeyDefinition>(keys, StringComparer.OrdinalIgnoreCase)
    };
}

internal sealed class HudKeyDefinition
{
    public string Action { get; set; } = string.Empty;
    public string? Accent { get; set; }

    public HudKeyDefinition() { }

    public HudKeyDefinition(string action, string? accent = null)
    {
        Action = action;
        Accent = accent;
    }
}

internal static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VorotexK15Hud";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Не удалось открыть HKCU Run.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
        key.SetValue(ValueName, $"\"{executable}\"");
    }
}

internal static class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const uint ModShift = 0x0004;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public static readonly IntPtr HwndTopMost = new(-1);
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
