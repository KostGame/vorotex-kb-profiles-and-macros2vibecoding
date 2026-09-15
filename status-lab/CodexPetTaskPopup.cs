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
        Height = 14 + rows * 30 + (_tasks.Count > rows ? 22 : 0);
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
            var palette = PetPaletteResolver.Resolve(_config, _profileHint, DateTimeOffset.UtcNow);
            var accent = CodexPetPopupPolicy.Accent(palette, row.VisualState);
            using var dot = new SolidBrush(accent);
            e.Graphics.FillEllipse(dot, 10, y + 9, 8, 8);
            using var title = new SolidBrush(Color.FromArgb(235, 235, 240, 244));
            using var subtitle = new SolidBrush(Color.FromArgb(165, 190, 198, 205));
            using var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var subtitleFont = new Font("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Pixel);
            e.Graphics.DrawString(row.DisplayTitle, titleFont, title, 26, y + 3);
            e.Graphics.DrawString(row.DisplaySubtitle ?? string.Empty, subtitleFont, subtitle, 26, y + 16);
            y += 30;
        }
        if (_tasks.Count > 5)
        {
            using var overflow = new SolidBrush(Color.FromArgb(175, 190, 198, 205));
            using var font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
            e.Graphics.DrawString("+ " + CodexPetPopupPolicy.OverflowCount(_tasks.Count) + " ещё", font, overflow, 10, y + 1);
        }
    }
}
