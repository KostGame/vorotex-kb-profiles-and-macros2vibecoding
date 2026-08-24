using System.Drawing.Drawing2D;

namespace Vorotex.K15.Hud;

internal sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer _autoHideTimer;
    private readonly string _hotkeyHint;
    private IReadOnlyList<ProfileDefinition> _profiles = Array.Empty<ProfileDefinition>();

    private static readonly Color BackgroundColor = Color.FromArgb(17, 21, 25);
    private static readonly Color PanelColor = Color.FromArgb(25, 32, 36);
    private static readonly Color MutedTextColor = Color.FromArgb(169, 184, 188);

    private readonly record struct ProfilePalette(
        Color Border,
        Color Title,
        Color Key,
        Color Primary,
        Color Flow,
        Color Send);

    public OverlayForm(int autoHideMs, string hotkeyHint)
    {
        _hotkeyHint = hotkeyHint;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BackgroundColor;
        Opacity = 0.97;
        DoubleBuffered = true;
        MinimumSize = new Size(640, 430);

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
        var logicalWidth = profiles.Count > 1 ? 1180 : 680;
        var logicalHeight = 455;
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
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var backgroundBrush = new SolidBrush(BackgroundColor);
        g.FillRectangle(backgroundBrush, ClientRectangle);

        var scale = Math.Max(1f, DeviceDpi / 96f);
        var outer = RectangleF.Inflate(ClientRectangle, -1 * scale, -1 * scale);
        var outerBorder = _profiles.Count == 1
            ? PaletteFor(_profiles[0]).Border
            : Color.FromArgb(105, 105, 122, 135);
        using var borderPen = new Pen(Color.FromArgb(145, outerBorder), 1.2f * scale);
        using var outerPath = RoundedRect(Rectangle.Round(outer), (int)(18 * scale));
        g.DrawPath(borderPen, outerPath);

        using var titleFont = new Font("Segoe UI Semibold", 17f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.WhiteSmoke);
        g.DrawString("VOROTEX K15 HUD", titleFont, titleBrush, 18 * scale, 13 * scale);

        using var hintFont = new Font("Segoe UI", 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var hintBrush = new SolidBrush(MutedTextColor);
        var hintSize = g.MeasureString(_hotkeyHint, hintFont);
        g.DrawString(_hotkeyHint, hintFont, hintBrush, Width - hintSize.Width - 18 * scale, 18 * scale);

        var contentTop = 52 * scale;
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
        var palette = PaletteFor(profile);

        using var panelBrush = new SolidBrush(PanelColor);
        using var panelPen = new Pen(Color.FromArgb(190, palette.Border), Math.Max(1f, scale));
        using var panelPath = RoundedRect(Rectangle.Round(panel), (int)(14 * scale));
        g.FillPath(panelBrush, panelPath);
        g.DrawPath(panelPen, panelPath);

        using var profileFont = new Font("Segoe UI Semibold", 15.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var profileBrush = new SolidBrush(palette.Title);
        g.DrawString($"ПРОФИЛЬ {profile.Id}  ·  {profile.Title}", profileFont, profileBrush,
            panel.Left + 13 * scale, panel.Top + 11 * scale);

        if (profile.TryGetKey("Encoder", out var encoder))
            DrawEncoderBadge(g, encoder.DisplayText, panel, palette, scale);

        var gridTop = panel.Top + 45 * scale;
        var innerLeft = panel.Left + 12 * scale;
        var innerWidth = panel.Width - 24 * scale;
        var colGap = 6 * scale;
        var rowGap = 8 * scale;
        var keyHeight = 82 * scale;
        var colWidth = (innerWidth - (5 * colGap)) / 6f;

        var row1 = new[] { "1", "2", "3", "4", "5", "6" };
        var row2 = new[] { "7", "8", "9", "0", ".", "Enter" };

        for (var i = 0; i < 6; i++)
        {
            var rect1 = new RectangleF(innerLeft + i * (colWidth + colGap), gridTop, colWidth, keyHeight);
            DrawKey(g, row1[i], profile.GetKey(row1[i]), rect1, scale, palette);

            var rect2 = new RectangleF(innerLeft + i * (colWidth + colGap), gridTop + keyHeight + rowGap, colWidth, keyHeight);
            DrawKey(g, row2[i], profile.GetKey(row2[i]), rect2, scale, palette);
        }

        var bottomTop = gridTop + (2 * keyHeight) + (2 * rowGap);
        var bottomHeight = Math.Max(106 * scale, panel.Bottom - bottomTop - 12 * scale);
        var unit = (innerWidth - (3 * colGap)) / 5.6f;
        var widths = new[] { unit, 1.2f * unit, 2.0f * unit, 1.4f * unit };
        var keys = new[] { "-", "+", "Space", "Joystick" };

        var x = innerLeft;
        for (var i = 0; i < keys.Length; i++)
        {
            var width = i == keys.Length - 1 ? innerLeft + innerWidth - x : widths[i];
            DrawKey(g, keys[i], profile.GetKey(keys[i]), new RectangleF(x, bottomTop, width, bottomHeight), scale, palette);
            x += width + colGap;
        }
    }

    private static void DrawEncoderBadge(
        Graphics g,
        string label,
        RectangleF panel,
        ProfilePalette palette,
        float scale)
    {
        var text = $"ENC: {label}";
        using var font = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        var measured = g.MeasureString(text, font);
        var width = measured.Width + 18 * scale;
        var height = 24 * scale;
        var rect = new RectangleF(
            panel.Right - width - 12 * scale,
            panel.Top + 8 * scale,
            width,
            height);

        using var fill = new SolidBrush(Color.FromArgb(72, palette.Key));
        using var pen = new Pen(Color.FromArgb(175, palette.Border), Math.Max(1f, scale));
        using var path = RoundedRect(Rectangle.Round(rect), (int)(10 * scale));
        g.FillPath(fill, path);
        g.DrawPath(pen, path);

        using var brush = new SolidBrush(palette.Title);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private static void DrawKey(
        Graphics g,
        string key,
        HudKeyDefinition definition,
        RectangleF rect,
        float scale,
        ProfilePalette palette)
    {
        var fill = definition.Accent?.ToLowerInvariant() switch
        {
            "primary" => palette.Primary,
            "flow" => palette.Flow,
            "send" => palette.Send,
            _ => palette.Key
        };

        using var shadowBrush = new SolidBrush(Color.FromArgb(75, 0, 0, 0));
        using var shadowPath = RoundedRect(
            Rectangle.Round(new RectangleF(rect.X + 2 * scale, rect.Y + 3 * scale, rect.Width, rect.Height)),
            (int)(8 * scale));
        g.FillPath(shadowBrush, shadowPath);

        using var keyBrush = new SolidBrush(fill);
        using var keyPen = new Pen(Color.FromArgb(185, palette.Border), Math.Max(1f, scale));
        using var keyPath = RoundedRect(Rectangle.Round(rect), (int)(8 * scale));
        g.FillPath(keyBrush, keyPath);
        g.DrawPath(keyPen, keyPath);

        using var keyFont = new Font("Segoe UI Semibold", 13.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Color.FromArgb(240, 245, 245, 245));
        using var actionBrush = new SolidBrush(Color.White);

        g.DrawString(DisplayKey(key), keyFont, labelBrush, rect.Left + 7 * scale, rect.Top + 6 * scale);

        var actionRect = new RectangleF(
            rect.Left + 7 * scale,
            rect.Top + 30 * scale,
            rect.Width - 14 * scale,
            rect.Height - 38 * scale);

        var displayText = definition.DisplayText;
        using var format = CreateActionStringFormat();
        using var actionFont = CreateActionFont(g, displayText, actionRect.Width, scale);

        var state = g.Save();
        g.SetClip(Rectangle.Round(actionRect));
        g.DrawString(displayText, actionFont, actionBrush, actionRect, format);
        g.Restore(state);
    }

    private static StringFormat CreateActionStringFormat() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit,
        Trimming = StringTrimming.EllipsisCharacter
    };

    private static Font CreateActionFont(Graphics g, string action, float availableWidth, float scale)
    {
        var size = ActionFontSize(action);
        const float minimumSize = 7.2f;
        var targetWidth = Math.Max(1f, availableWidth - 2f * scale);

        while (size > minimumSize)
        {
            using var probe = new Font("Segoe UI Semibold", size * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            var measuredWidth = g.MeasureString(action, probe, int.MaxValue, StringFormat.GenericTypographic).Width;
            if (measuredWidth <= targetWidth)
                return new Font("Segoe UI Semibold", size * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            size -= 0.35f;
        }

        return new Font("Segoe UI Semibold", minimumSize * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static float ActionFontSize(string action)
    {
        var length = action.Length;
        return length switch
        {
            > 24 => 11.4f,
            > 17 => 12.2f,
            > 12 => 13.2f,
            _ => 14.4f
        };
    }

    private static ProfilePalette PaletteFor(ProfileDefinition profile) =>
        profile.Color.Trim().ToLowerInvariant() switch
        {
            "red" => new ProfilePalette(
                Color.FromArgb(214, 76, 78),
                Color.FromArgb(255, 111, 112),
                Color.FromArgb(112, 39, 44),
                Color.FromArgb(145, 50, 55),
                Color.FromArgb(92, 34, 39),
                Color.FromArgb(174, 62, 57)),
            "blue" => new ProfilePalette(
                Color.FromArgb(74, 132, 214),
                Color.FromArgb(103, 169, 255),
                Color.FromArgb(30, 78, 126),
                Color.FromArgb(38, 101, 164),
                Color.FromArgb(27, 69, 109),
                Color.FromArgb(49, 117, 182)),
            _ => new ProfilePalette(
                Color.FromArgb(55, 218, 210),
                Color.FromArgb(83, 231, 224),
                Color.FromArgb(22, 97, 101),
                Color.FromArgb(21, 126, 130),
                Color.FromArgb(37, 101, 103),
                Color.FromArgb(42, 145, 146))
        };

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
