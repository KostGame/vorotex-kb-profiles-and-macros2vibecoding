using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetWindow : Form
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private readonly CodexPetPositionStore _positionStore;
    private readonly StatusLabConfig _config;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ProfileColorHint _profileHint;
    private CodexPetVisualState _state;
    private double _stateEnteredSeconds;
    private CodexPetPresentation _presentation = new(
        new(CodexPetVisualState.Idle, "initial", null, null), Array.Empty<CodexPetTaskRow>());
    private readonly CodexPetTaskPopup _popup;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    internal event Action? CloseRequested;

    public CodexPetWindow(StatusLabConfig config, CodexPetPositionStore? positionStore = null)
    {
        _config = config;
        _profileHint = ProfileColorHint.Unknown(DateTimeOffset.UtcNow);
        _positionStore = positionStore ?? new CodexPetPositionStore();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(128, 128);
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 30);
        TransparencyKey = BackColor;
        DoubleBuffered = true;
        ContextMenuStrip = BuildContextMenu();
        _popup = new CodexPetTaskPopup(this);
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        _animation.Tick += (_, _) => Invalidate();
        _animation.Start();
    }

    public void SetState(CodexPetVisualState state)
    {
        if (_state != state)
        {
            _stateEnteredSeconds = _clock.Elapsed.TotalSeconds;
        }
        _state = state;
        Invalidate();
    }

    public void SetPresentation(CodexPetPresentation presentation)
    {
        _presentation = presentation;
        _popup.SetPresentation(presentation);
        Invalidate();
    }

    internal void HidePopup() => _popup.Hide();

    public void SetProfileHint(ProfileColorHint hint)
    {
        _profileHint = hint;
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var areas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        Location = CodexPetPositionPolicy.Resolve(_positionStore.Load(), Size, areas);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Закрыть питомца", null, (_, _) => CloseRequested?.Invoke());
        return menu;
    }

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (BadgeBounds().Contains(e.Location))
        {
            _popup.Toggle(_presentation.RelevantTaskCount);
            return;
        }
        if (_popup.Visible) _popup.Hide();
        _dragging = true;
        _dragMoved = false;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        Capture = true;
    }

    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || !e.Button.HasFlag(MouseButtons.Left)) return;
        var cursor = Cursor.Position;
        var delta = new Size(cursor.X - _dragStartCursor.X, cursor.Y - _dragStartCursor.Y);
        if (!_dragMoved && Math.Abs(delta.Width) < 2 && Math.Abs(delta.Height) < 2) return;
        _dragMoved = true;
        Location = new Point(_dragStartLocation.X + delta.Width, _dragStartLocation.Y + delta.Height);
    }

    private void HandleMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_dragging) return;
        _dragging = false;
        Capture = false;
        if (_dragMoved)
        {
            try { _positionStore.Save(new CodexPetPosition(Location.X, Location.Y)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A transient per-user persistence failure must not break pet interaction.
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var motion = CodexPetMotion.Sample(_state, _clock.Elapsed.TotalSeconds - _stateEnteredSeconds);
        var body = new Rectangle(11 + (int)Math.Round(motion.OffsetX), 20 + (int)Math.Round(motion.OffsetY), 106, 88);
        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        FillRounded(g, new Rectangle(body.X + 3, body.Y + 5, body.Width, body.Height), 11, shadow);
        using var chassis = new SolidBrush(Color.FromArgb(40, 45, 54));
        using var outline = new Pen(Color.FromArgb(112, 125, 140), 2);
        FillRounded(g, body, 11, chassis);
        DrawRounded(g, body, 11, outline);
        using var bandDivider = new Pen(Color.FromArgb(92, 112, 126, 136), 1);
        g.DrawLine(bandDivider, body.Left + 6, body.Top + body.Height * 35 / 100, body.Right - 6, body.Top + body.Height * 35 / 100);
        g.DrawLine(bandDivider, body.Left + 6, body.Top + body.Height * 60 / 100, body.Right - 6, body.Top + body.Height * 60 / 100);

        var intent = VisualEffectIntentFactory.ForPet(_state, _config);
        var palette = PetPaletteResolver.Resolve(_config, _profileHint, DateTimeOffset.UtcNow);
        var controls = MiniK15ControlLayout.Controls;
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            var bounds = ControlBounds(body, control);
            var sample = VisualEffectAnimator.Sample(intent, _clock.Elapsed.TotalSeconds, index, controls.Count);
            DrawControl(g, control, bounds, sample, palette);
        }

        DrawBadge(g, BadgeBounds(), _presentation.RelevantTaskCount, palette);

    }

    private Rectangle BadgeBounds() => new(92, 5, 30, 18);

    private static void DrawBadge(Graphics graphics, Rectangle bounds, int count, PetPalette palette)
    {
        if (count == 0) return;
        var accent = palette.IsNeutral ? Color.FromArgb(103, 139, 153) :
            Color.FromArgb(palette.Primary.R, palette.Primary.G, palette.Primary.B);
        using var fill = new SolidBrush(Color.FromArgb(238, 31, 36, 45));
        using var outline = new Pen(Color.FromArgb(210, accent), 1);
        FillRounded(graphics, bounds, 8, fill);
        DrawRounded(graphics, bounds, 8, outline);
        using var text = new SolidBrush(Color.FromArgb(235, accent));
        using var font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(CodexPetAdapter.FormatTaskCount(count), font, text, bounds, format);
    }

    private static Rectangle ControlBounds(Rectangle body, MiniK15Control control) => new(
        body.X + control.X * body.Width / 100,
        body.Y + control.Y * body.Height / 100,
        Math.Max(4, control.Width * body.Width / 100),
        Math.Max(4, control.Height * body.Height / 100));

    private static void DrawControl(Graphics graphics, MiniK15Control control, Rectangle bounds, VisualEffectSample sample,
        PetPalette palette)
    {
        using var controlFill = new SolidBrush(KeyColor(sample, palette));
        using var controlOutline = new Pen(Color.FromArgb(24, 28, 35), 1);
        if (control.Kind is MiniK15ControlKind.Rotary or MiniK15ControlKind.Joystick)
        {
            var size = Math.Min(bounds.Width, bounds.Height);
            bounds = new Rectangle(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
            graphics.FillEllipse(controlFill, bounds);
            graphics.DrawEllipse(controlOutline, bounds);
            var inset = control.Kind == MiniK15ControlKind.Rotary ? 4 : 3;
            using var detail = new Pen(Color.FromArgb(145, 180, 190, 198), 1);
            graphics.DrawEllipse(detail, Rectangle.Inflate(bounds, -inset, -inset));
            if (control.Kind == MiniK15ControlKind.Joystick)
            {
                var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                graphics.DrawLine(detail, center.X - 3, center.Y, center.X + 3, center.Y);
                graphics.DrawLine(detail, center.X, center.Y - 3, center.X, center.Y + 3);
            }
            return;
        }

        var radius = control.Kind == MiniK15ControlKind.LongBottomKey ? 4 : 3;
        FillRounded(graphics, bounds, radius, controlFill);
        DrawRounded(graphics, bounds, radius, controlOutline);
        if (!string.IsNullOrWhiteSpace(control.Label))
        {
            using var label = new SolidBrush(Color.FromArgb(215, 235, 240, 244));
            using var font = new Font("Segoe UI", 6f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(control.Label, font, label, bounds, format);
        }
    }

    private static Color KeyColor(VisualEffectSample sample, PetPalette palette)
    {
        var amount = (int)Math.Round(Math.Clamp(sample.Intensity, 0d, 1d) * 255d);
        var tone = Math.Clamp(sample.Tone, 0d, 1d);
        if (!palette.IsNeutral)
        {
            var profile = palette.Primary;
            var lift = 0.12d + tone * 0.18d;
            return Color.FromArgb(Math.Max(24, amount),
                Blend(profile.R, 255, lift), Blend(profile.G, 255, lift), Blend(profile.B, 255, lift));
        }
        var red = (int)Math.Round(82 + 42 * tone);
        var green = (int)Math.Round(115 + 48 * tone);
        var blue = (int)Math.Round(128 + 45 * tone);
        return Color.FromArgb(Math.Max(24, amount), red, green, blue);
    }

    private static int Blend(byte value, byte target, double amount) =>
        (int)Math.Round(value + (target - value) * Math.Clamp(amount, 0d, 1d));

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup.Dispose();
            _animation.Dispose();
        }
        base.Dispose(disposing);
    }

}

internal sealed class CodexPetController : IDisposable
{
    private readonly JournalStateNormalizer _normalizer;
    private readonly ICodexUnreadStateReader _reader;
    private readonly CodexPetWindow _window;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private bool _visible;

    public CodexPetController(JournalStateNormalizer normalizer, ICodexUnreadStateReader reader, StatusLabConfig config)
    {
        _normalizer = normalizer;
        _reader = reader;
        _window = new CodexPetWindow(config);
        _window.CloseRequested += HidePet;
        _normalizer.StateChanged += OnStateChanged;
        _refresh.Tick += (_, _) => Refresh();
        Refresh();
    }

    public void SetProfileHint(ProfileColorHint hint)
    {
        if (_window.IsDisposed) return;
        if (_window.IsHandleCreated && _window.InvokeRequired)
        {
            try { _window.BeginInvoke(() => _window.SetProfileHint(hint)); } catch (InvalidOperationException) { }
            return;
        }
        _window.SetProfileHint(hint);
    }

    public bool Visible => _visible;
    public void Toggle()
    {
        if (_visible) HidePet(); else ShowPet();
    }

    private void ShowPet()
    {
        _visible = true;
        _window.Show();
        Refresh();
    }

    private void HidePet()
    {
        _visible = false;
        _window.HidePopup();
        _window.Hide();
        if (_refresh.Enabled && !CodexPetAdapter.ShouldPollUnread(_normalizer.SessionSnapshots)) _refresh.Stop();
    }

    private void OnStateChanged(K15NormalizedState _, StateTransition? __)
    {
        if (_window.IsDisposed) return;
        if (_window.IsHandleCreated && _window.InvokeRequired)
        {
            try { _window.BeginInvoke(Refresh); } catch (InvalidOperationException) { }
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_window.IsDisposed) return;
        var sessions = _normalizer.SessionSnapshots;
        var canPollUnread = CodexPetAdapter.ShouldPollUnread(sessions);
        // The existing bounded refresh also catches per-session list changes
        // while the pet is visible; it never performs animation work.
        if ((canPollUnread || _visible) && !_refresh.Enabled) _refresh.Start();
        if (!canPollUnread && !_visible && _refresh.Enabled) _refresh.Stop();
        // One canonical reader snapshot is shared by every eligible DONE thread.
        var unreadSnapshot = canPollUnread ? _reader.Read(DateTimeOffset.UtcNow) : null;
        var presentation = CodexPetAdapter.MapPresentation(sessions, unreadSnapshot);
        _window.SetPresentation(presentation);
        _window.SetState(presentation.Global.State);
    }

    public void Dispose()
    {
        _normalizer.StateChanged -= OnStateChanged;
        _window.CloseRequested -= HidePet;
        _refresh.Dispose();
        _window.Close();
        _window.Dispose();
    }
}
