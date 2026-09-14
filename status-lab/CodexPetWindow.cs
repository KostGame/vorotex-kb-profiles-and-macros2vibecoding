using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetWindow : Form
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 80 };
    private readonly CodexPetPositionStore _positionStore;
    private readonly StatusLabConfig _config;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CodexPetVisualState _state;
    private int _phase;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    internal event Action? CloseRequested;

    public CodexPetWindow(StatusLabConfig config, CodexPetPositionStore? positionStore = null)
    {
        _config = config;
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
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        _animation.Tick += (_, _) => { _phase = (_phase + 1) % 8; Invalidate(); };
        _animation.Start();
    }

    public void SetState(CodexPetVisualState state)
    {
        _state = state;
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
        var bob = _state == CodexPetVisualState.Idle ? (_phase % 4 == 0 ? 1 : 0) : (_phase % 2);
        var body = new Rectangle(11, 20 + bob, 106, 88);
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
        var controls = MiniK15ControlLayout.Controls;
        for (var index = 0; index < controls.Count; index++)
        {
            var control = controls[index];
            var bounds = ControlBounds(body, control);
            var sample = VisualEffectAnimator.Sample(intent, _clock.Elapsed.TotalSeconds, index, controls.Count);
            DrawControl(g, control, bounds, sample);
        }

    }

    private static Rectangle ControlBounds(Rectangle body, MiniK15Control control) => new(
        body.X + control.X * body.Width / 100,
        body.Y + control.Y * body.Height / 100,
        Math.Max(4, control.Width * body.Width / 100),
        Math.Max(4, control.Height * body.Height / 100));

    private static void DrawControl(Graphics graphics, MiniK15Control control, Rectangle bounds, VisualEffectSample sample)
    {
        using var controlFill = new SolidBrush(KeyColor(sample));
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

    private static Color KeyColor(VisualEffectSample sample)
    {
        var amount = (int)Math.Round(Math.Clamp(sample.Intensity, 0d, 1d) * 255d);
        var tone = Math.Clamp(sample.Tone, 0d, 1d);
        var red = (int)Math.Round(82 + 42 * tone);
        var green = (int)Math.Round(115 + 48 * tone);
        var blue = (int)Math.Round(128 + 45 * tone);
        return Color.FromArgb(Math.Max(24, amount), red, green, blue);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
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

    public bool Visible => _visible;
    public void Toggle()
    {
        if (_visible) HidePet(); else ShowPet();
    }

    private void ShowPet()
    {
        _visible = true;
        _window.Show();
    }

    private void HidePet()
    {
        _visible = false;
        _window.Hide();
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
        if (canPollUnread && !_refresh.Enabled) _refresh.Start();
        if (!canPollUnread && _refresh.Enabled) _refresh.Stop();
        // One canonical reader snapshot is shared by every eligible DONE thread.
        var unreadSnapshot = canPollUnread ? _reader.Read(DateTimeOffset.UtcNow) : null;
        var snapshot = CodexPetAdapter.Map(sessions, unreadSnapshot);
        _window.SetState(snapshot.State);
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
