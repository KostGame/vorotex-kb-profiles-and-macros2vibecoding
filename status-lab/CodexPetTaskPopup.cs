using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetTaskPopup : Form
{
    private readonly CodexPetWindow _pet;
    private readonly StatusLabConfig _config;
    private IReadOnlyList<CodexPetTaskRow> _tasks = Array.Empty<CodexPetTaskRow>();
    private ProfileColorHint _profileHint;
    private bool _open;

    internal CodexPetTaskPopup(CodexPetWindow pet, StatusLabConfig config)
    {
        _pet = pet;
        _config = config;
        _profileHint = ProfileColorHint.Unknown(DateTimeOffset.UtcNow);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 30);
        DoubleBuffered = true;
        Deactivate += (_, _) =>
        {
            // Deactivation can precede the owner's badge MouseDown. Defer the
            // secondary click-outside close so badge toggle sees the explicit
            // open state and cannot reopen a popup it just closed.
            if (!_open || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (_open) ClosePopup();
            });
        };
    }

    internal void SetPresentation(CodexPetPresentation presentation)
    {
        _tasks = presentation.Tasks;
        Invalidate();
        if (Visible) PositionNearPet();
    }

    internal void SetProfileHint(ProfileColorHint hint)
    {
        _profileHint = hint;
        Invalidate();
    }

    internal void Toggle(int count)
    {
        if (_open)
        {
            ClosePopup();
            return;
        }
        if (count == 0) return;
        PositionNearPet();
        _open = true;
        Show(_pet);
    }

    internal void ClosePopup()
    {
        _open = false;
        Hide();
    }

    private void PositionNearPet()
    {
        var rows = CodexPetPopupPolicy.VisibleRows(_tasks.Count);
        Height = 14 + rows * 34 + (_tasks.Count > rows ? 22 : 0);
        Width = 220;
        var area = Screen.FromControl(_pet).WorkingArea;
        var desired = new Point(_pet.Right + 8, _pet.Top);
        if (desired.X + Width > area.Right) desired.X = _pet.Left - Width - 8;
        Location = CodexPetPopupPolicy.ClampToWorkingArea(desired, Size, area);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(245, 31, 36, 45));
        using var border = new Pen(Color.FromArgb(180, 92, 112, 126), 1);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var y = 7;
        foreach (var row in _tasks.Take(5))
        {
            var glyph = CodexPetPopupPolicy.GlyphFor(row.VisualState);
            DrawGlyph(e.Graphics, glyph, new Point(14, y + 14));
            using var title = new SolidBrush(Color.FromArgb(235, 235, 240, 244));
            using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
            e.Graphics.DrawString(row.DisplayTitle, titleFont, title, 28, y + 3);
            if (!string.IsNullOrWhiteSpace(row.DisplaySubtitle))
            {
                using var subtitle = new SolidBrush(Color.FromArgb(170, 190, 198, 205));
                using var subtitleFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
                e.Graphics.DrawString(row.DisplaySubtitle, subtitleFont, subtitle, 28, y + 18);
            }
            y += 34;
        }
        if (_tasks.Count > 5)
        {
            using var overflow = new SolidBrush(Color.FromArgb(175, 190, 198, 205));
            using var font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
            e.Graphics.DrawString("+ " + CodexPetPopupPolicy.OverflowCount(_tasks.Count) + " ещё", font, overflow, 10, y + 1);
        }
    }

    private static void DrawGlyph(Graphics graphics, CodexPetPopupPolicy.TaskStatusGlyphStyle glyph, Point center)
    {
        using var brush = new SolidBrush(glyph.SemanticColor);
        using var pen = new Pen(glyph.SemanticColor, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (glyph.Shape)
        {
            case CodexPetPopupPolicy.TaskStatusGlyphShape.Dot:
                graphics.FillEllipse(brush, center.X - 5, center.Y - 5, 10, 10);
                break;
            case CodexPetPopupPolicy.TaskStatusGlyphShape.AttentionCircle:
                graphics.DrawEllipse(pen, center.X - 6, center.Y - 6, 12, 12);
                graphics.DrawLine(pen, center.X, center.Y - 3, center.X, center.Y + 1);
                graphics.FillEllipse(brush, center.X - 1, center.Y + 3, 2, 2);
                break;
            case CodexPetPopupPolicy.TaskStatusGlyphShape.Check:
                graphics.DrawLines(pen, new Point[] { new Point(center.X - 6, center.Y), new Point(center.X - 2, center.Y + 4), new Point(center.X + 6, center.Y - 5) });
                break;
        }
    }
}
