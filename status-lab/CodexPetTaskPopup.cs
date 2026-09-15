using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetTaskPopup : Form
{
    private readonly CodexPetWindow _pet;
    private IReadOnlyList<CodexPetTaskRow> _tasks = Array.Empty<CodexPetTaskRow>();

    internal CodexPetTaskPopup(CodexPetWindow pet)
    {
        _pet = pet;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 30);
        DoubleBuffered = true;
        Deactivate += (_, _) => Hide();
    }

    internal void SetPresentation(CodexPetPresentation presentation)
    {
        _tasks = presentation.Tasks;
        Invalidate();
        if (Visible) PositionNearPet();
    }

    internal void Toggle(int count)
    {
        if (Visible) { Hide(); return; }
        if (count == 0) return;
        PositionNearPet();
        Show(_pet);
    }

    private void PositionNearPet()
    {
        var rows = Math.Min(5, _tasks.Count);
        Height = 14 + rows * 30 + (_tasks.Count > rows ? 22 : 0);
        Width = 220;
        var area = Screen.FromControl(_pet).WorkingArea;
        var desired = new Point(_pet.Right + 8, _pet.Top);
        if (desired.X + Width > area.Right) desired.X = _pet.Left - Width - 8;
        desired.X = Math.Clamp(desired.X, area.Left, area.Right - Width);
        desired.Y = Math.Clamp(desired.Y, area.Top, area.Bottom - Height);
        Location = desired;
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
            var accent = row.VisualState switch
            {
                CodexPetVisualState.Waiting => Color.FromArgb(232, 194, 94),
                CodexPetVisualState.Review => Color.FromArgb(130, 190, 202),
                _ => Color.FromArgb(125, 165, 178)
            };
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
            e.Graphics.DrawString("+ " + (_tasks.Count - 5) + " ещё", font, overflow, 10, y + 1);
        }
    }
}
