using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetTaskPopup : Form
{
    private const int CardWidth = 296;
    private const int CardHeight = 54;

    private readonly CodexPetWindow _pet;
    private readonly StatusLabConfig _config;
    private IReadOnlyList<CodexPetTaskRow> _tasks = Array.Empty<CodexPetTaskRow>();
    private ProfileColorHint _profileHint;
    private PetTaskSurfaceState _surfaceState = CodexPetTaskSurfacePolicy.DefaultState;

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
        Cursor = Cursors.Default;
    }

    internal void SetPresentation(CodexPetPresentation presentation)
    {
        _tasks = presentation.Tasks;
        ApplySurfaceState(_surfaceState, _tasks.Count);
        Invalidate();
    }

    internal void SetProfileHint(ProfileColorHint hint)
    {
        _profileHint = hint;
        Invalidate();
    }

    internal void SetSurfaceState(PetTaskSurfaceState state, int count)
    {
        _surfaceState = CodexPetTaskSurfacePolicy.Select(state);
        ApplySurfaceState(_surfaceState, count);
    }

    internal void ApplySurfaceState(PetTaskSurfaceState state, int count)
    {
        _surfaceState = CodexPetTaskSurfacePolicy.Select(state);
        if (!CodexPetTaskSurfacePolicy.IsVisible(_surfaceState, count) || !_pet.Visible)
        {
            ClosePopup();
            return;
        }

        PositionNearPet();
        if (!Visible) Show(_pet);
        Invalidate();
    }

    internal void Reanchor()
    {
        if (Visible) PositionNearPet();
    }

    internal void ClosePopup() => Hide();

    private void PositionNearPet()
    {
        var layout = CodexPetTaskSurfacePolicy.Layout(_surfaceState, _tasks.Count,
            new Size(CardWidth, CardHeight));
        Size = layout.PanelSize;
        if (layout.PanelSize == Size.Empty) return;

        var monitor = Screen.FromRectangle(_pet.Bounds);
        var placement = TaskPanelPlacementPolicy.Place(
            _pet.Bounds, layout.PanelSize, monitor.WorkingArea);
        Location = placement.Bounds.Location;
        Size = placement.Bounds.Size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(245, 31, 36, 45));
        using var border = new Pen(Color.FromArgb(180, 92, 112, 126), 1);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        var layout = CodexPetTaskSurfacePolicy.Layout(_surfaceState, _tasks.Count,
            new Size(CardWidth, CardHeight));
        var palette = PetPaletteResolver.Resolve(_config, _profileHint, DateTimeOffset.UtcNow);
        foreach (var card in layout.Cards)
        {
            var row = card.TaskIndex >= 0 && card.TaskIndex < _tasks.Count ? _tasks[card.TaskIndex] : null;
            DrawCard(e.Graphics, card, row, palette);
        }

        if (layout.State == PetTaskSurfaceState.Expanded && layout.OverflowCount > 0)
        {
            using var overflow = new SolidBrush(Color.FromArgb(190, 190, 198, 205));
            using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
            var y = 12 + CodexPetTaskSurfacePolicy.ExpandedVisibleRows(_tasks.Count) * (CardHeight + 8);
            e.Graphics.DrawString("+" + layout.OverflowCount + " ещё", font, overflow, 18, y);
        }
    }

    private static void DrawCard(Graphics graphics, TaskCardLayout card, CodexPetTaskRow? row,
        PetPalette palette)
    {
        var fillColor = card.IsTopCard
            ? Color.FromArgb(248, 46, 53, 65)
            : Color.FromArgb(190, 39, 45, 56);
        var accent = row is null
            ? Color.FromArgb(95, 116, 132)
            : CodexPetPopupPolicy.Accent(palette, row.VisualState);
        using var fill = new SolidBrush(fillColor);
        using var outline = new Pen(Color.FromArgb(card.IsTopCard ? 220 : 130, accent), 1);
        FillRounded(graphics, card.Bounds, 10, fill);
        DrawRounded(graphics, card.Bounds, 10, outline);
        if (!card.ShowsText || row is null) return;

        var glyph = CodexPetPopupPolicy.GlyphFor(row.VisualState);
        DrawGlyph(graphics, glyph, new Point(card.Bounds.Left + 17, card.Bounds.Top + 27));
        var textBounds = new Rectangle(card.Bounds.Left + 32, card.Bounds.Top + 7,
            card.Bounds.Width - 42, 22);
        using var title = new SolidBrush(Color.FromArgb(240, 235, 240, 244));
        using var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        graphics.DrawString(row.DisplayTitle, titleFont, title, textBounds, titleFormat);
        if (!string.IsNullOrWhiteSpace(row.DisplaySubtitle))
        {
            using var subtitle = new SolidBrush(Color.FromArgb(180, 190, 198, 205));
            using var subtitleFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
            var subtitleBounds = new Rectangle(textBounds.X, card.Bounds.Top + 29,
                textBounds.Width, 17);
            graphics.DrawString(row.DisplaySubtitle, subtitleFont, subtitle, subtitleBounds,
                titleFormat);
        }
    }

    private static void DrawGlyph(Graphics graphics, CodexPetPopupPolicy.TaskStatusGlyphStyle glyph,
        Point center)
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
                graphics.DrawLines(pen, new Point[]
                {
                    new(center.X - 6, center.Y),
                    new(center.X - 2, center.Y + 4),
                    new(center.X + 6, center.Y - 5)
                });
                break;
        }
    }

    private static void FillRounded(Graphics graphics, Rectangle rectangle, int radius, Brush brush)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRounded(Graphics graphics, Rectangle rectangle, int radius, Pen pen)
    {
        using var path = RoundedPath(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
