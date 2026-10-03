using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetTaskPopup : Form
{
    private const int CardWidth = CodexPetTaskSurfacePolicy.DefaultCardWidth;
    private const int CardHeight = CodexPetTaskSurfacePolicy.DefaultCardHeight;

    private readonly CodexPetWindow _pet;
    private readonly StatusLabConfig _config;
    private IReadOnlyList<CodexPetTaskRow> _tasks = Array.Empty<CodexPetTaskRow>();
    private ProfileColorHint _profileHint;
    private PetTaskSurfaceState _surfaceState = CodexPetTaskSurfacePolicy.DefaultState;
    private readonly Action<string> _launchThreadLink;
    private readonly System.Windows.Forms.Timer _waitingPulseTimer;
    private readonly Stopwatch _waitingPulseClock = Stopwatch.StartNew();

    internal CodexPetTaskPopup(CodexPetWindow pet, StatusLabConfig config,
        Action<string>? launchThreadLink = null)
    {
        _pet = pet;
        _config = config;
        _profileHint = ProfileColorHint.Unknown(DateTimeOffset.UtcNow);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowTransparency = true;
        BackColor = CodexPetTaskSurfacePolicy.HostContract.TransparencyKey;
        TransparencyKey = CodexPetTaskSurfacePolicy.HostContract.TransparencyKey;
        DoubleBuffered = true;
        Cursor = Cursors.Default;
        _launchThreadLink = launchThreadLink ?? LaunchThreadLink;
        MouseMove += HandleMouseMove;
        MouseLeave += (_, _) => Cursor = Cursors.Default;
        MouseClick += HandleMouseClick;
        _waitingPulseTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _waitingPulseTimer.Tick += (_, _) =>
        {
            if (!Visible || !_tasks.Any(row => row.VisualState == CodexPetVisualState.Waiting))
            {
                _waitingPulseTimer.Stop();
                return;
            }
            Invalidate();
        };
    }

    internal void SetPresentation(CodexPetPresentation presentation)
    {
        _tasks = presentation.Tasks;
        ApplySurfaceState(_surfaceState, _tasks.Count);
        UpdateWaitingPulseTimer();
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
        UpdateWaitingPulseTimer();
        Invalidate();
    }

    internal void Reanchor()
    {
        if (Visible) PositionNearPet();
    }

    internal void ClosePopup()
    {
        _waitingPulseTimer.Stop();
        ReplaceRegion(null);
        Size = Size.Empty;
        Hide();
    }

    private void UpdateWaitingPulseTimer()
    {
        if (Visible && _tasks.Any(row => row.VisualState == CodexPetVisualState.Waiting))
            _waitingPulseTimer.Start();
        else
            _waitingPulseTimer.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _waitingPulseTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Clear with the color key so invalidated pixels become transparent instead of
        // retaining stale card/background pixels between relayouts.
        e.Graphics.Clear(CodexPetTaskSurfacePolicy.HostContract.TransparencyKey);
    }

    private void ReplaceRegion(Region? next)
    {
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private void PositionNearPet()
    {
        var layout = CodexPetTaskSurfacePolicy.Layout(_surfaceState, _tasks.Count,
            new Size(CardWidth, CardHeight));
        Size = layout.PanelSize;
        ReplaceRegion(CodexPetTaskSurfaceRegion.Build(layout));
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
        var layout = CodexPetTaskSurfacePolicy.Layout(_surfaceState, _tasks.Count,
            new Size(CardWidth, CardHeight));
        var palette = PetPaletteResolver.Resolve(_config, _profileHint, DateTimeOffset.UtcNow);
        var pulseElapsedSeconds = _waitingPulseClock.Elapsed.TotalSeconds;
        foreach (var card in layout.Cards)
        {
            var row = card.TaskIndex >= 0 && card.TaskIndex < _tasks.Count ? _tasks[card.TaskIndex] : null;
            DrawCard(e.Graphics, card, row, palette, pulseElapsedSeconds);
        }

        if (layout.State == PetTaskSurfaceState.Expanded && layout.OverflowCount > 0)
        {
            using var overflow = new SolidBrush(Color.FromArgb(190, 190, 198, 205));
            using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
            var overflowBounds = CodexPetTaskSurfacePolicy.OverflowBounds(layout,
                new Size(CardWidth, CardHeight));
            e.Graphics.DrawString("+" + layout.OverflowCount + " ещё", font, overflow,
                overflowBounds);
        }
    }

    private void HandleMouseMove(object? sender, MouseEventArgs e) =>
        Cursor = FindOpenTarget(e.Location) is null ? Cursors.Default : Cursors.Hand;

    private void HandleMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || FindOpenTarget(e.Location) is not { } target) return;
        try
        {
            CodexPetTaskLinkPolicy.TryLaunch(target.DeepLink, target.CanFocus, _launchThreadLink);
        }
        catch
        {
            // A missing or failed protocol handler is presentation-only.
        }
    }

    private CodexActivityOpenTarget? FindOpenTarget(Point point)
    {
        var layout = CodexPetTaskSurfacePolicy.Layout(_surfaceState, _tasks.Count,
            new Size(CardWidth, CardHeight));
        foreach (var card in layout.Cards)
        {
            if (!card.ShowsText || !CodexPetTaskSurfaceRegion.IsPointInRoundedCard(card.Bounds, point) ||
                card.TaskIndex < 0 || card.TaskIndex >= _tasks.Count) continue;
            var target = _tasks[card.TaskIndex].Activity.OpenTarget;
            if (target.Source == CodexActivitySource.Local && target.CanFocus &&
                CodexPetTaskLinkPolicy.IsExactCodexThreadLink(target.DeepLink)) return target;
        }
        return null;
    }

    private static void LaunchThreadLink(string deepLink) =>
        Process.Start(new ProcessStartInfo { FileName = deepLink, UseShellExecute = true });

    private static void DrawCard(Graphics graphics, TaskCardLayout card, CodexPetTaskRow? row,
        PetPalette palette, double pulseElapsedSeconds)
    {
        var fillColor = card.IsTopCard
            ? Color.FromArgb(248, 46, 53, 65)
            : Color.FromArgb(190, 39, 45, 56);
        var accent = row is null
            ? Color.FromArgb(95, 116, 132)
            : CodexPetPopupPolicy.Accent(palette, row.VisualState);
        var outlineStyle = CodexPetPopupPolicy.OutlineFor(
            row?.VisualState ?? CodexPetVisualState.Idle, card.IsTopCard, pulseElapsedSeconds);
        using var fill = new SolidBrush(fillColor);
        using var outline = new Pen(Color.FromArgb(outlineStyle.Alpha, accent), outlineStyle.Width);
        FillRounded(graphics, card.Bounds, 10, fill);
        DrawRounded(graphics, card.Bounds, 10, outline);
        if (!card.ShowsText || row is null) return;

        var glyph = CodexPetPopupPolicy.GlyphFor(row.VisualState);
        DrawGlyph(graphics, glyph, new Point(card.Bounds.Left + 17,
            card.Bounds.Top + card.Bounds.Height / 2));
        var textBounds = CodexPetTaskTypography.TitleBounds(card.Bounds);
        using var title = new SolidBrush(Color.FromArgb(240, 235, 240, 244));
        using var titleFont = new Font("Segoe UI", CodexPetTaskTypography.TitlePixelSize,
            FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit,
            Trimming = StringTrimming.None
        };
        var titleLayout = CodexPetTaskTypography.LayoutTitle(
            graphics, titleFont, row.DisplayTitle, textBounds);
        for (var line = 0; line < titleLayout.Lines.Count; line++)
        {
            graphics.DrawString(titleLayout.Lines[line], titleFont, title,
                new RectangleF(textBounds.Left,
                    textBounds.Top + line * CodexPetTaskTypography.TitleLineHeight,
                    textBounds.Width, CodexPetTaskTypography.TitleLineHeight), titleFormat);
        }
        if (!string.IsNullOrWhiteSpace(row.DisplaySubtitle))
        {
            using var subtitle = new SolidBrush(Color.FromArgb(CodexPetTaskTypography.SubtitleAlpha,
                198, 207, 214));
            using var subtitleFont = new Font("Segoe UI", CodexPetTaskTypography.SubtitlePixelSize,
                FontStyle.Regular, GraphicsUnit.Pixel);
            var subtitleBounds = CodexPetTaskTypography.SubtitleBounds(card.Bounds);
            using var subtitleFormat = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit,
                Trimming = StringTrimming.EllipsisCharacter
            };
            graphics.DrawString(row.DisplaySubtitle, subtitleFont, subtitle, subtitleBounds,
                subtitleFormat);
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
