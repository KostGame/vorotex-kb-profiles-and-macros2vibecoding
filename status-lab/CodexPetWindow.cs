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
        var body = new Rectangle(13, 32 + bob, 102, 62);
        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        FillRounded(g, new Rectangle(body.X + 3, body.Y + 5, body.Width, body.Height), 11, shadow);
        using var chassis = new SolidBrush(Color.FromArgb(40, 45, 54));
        using var outline = new Pen(Color.FromArgb(112, 125, 140), 2);
        FillRounded(g, body, 11, chassis);
        DrawRounded(g, body, 11, outline);

        var intent = VisualEffectIntentFactory.ForPet(_state, _config);
        var keys = KeyLayout(body);
        for (var index = 0; index < keys.Count; index++)
        {
            var sample = VisualEffectAnimator.Sample(intent, _clock.Elapsed.TotalSeconds, index, keys.Count);
            using var keyFill = new SolidBrush(KeyColor(sample));
            using var keyOutline = new Pen(Color.FromArgb(24, 28, 35), 1);
            FillRounded(g, keys[index], 3, keyFill);
            DrawRounded(g, keys[index], 3, keyOutline);
        }

        var indicator = VisualEffectAnimator.Sample(intent, _clock.Elapsed.TotalSeconds, 0, keys.Count);
        using var indicatorFill = new SolidBrush(KeyColor(indicator));
        g.FillEllipse(indicatorFill, body.Right - 15, body.Y + 7, 5, 5);
    }

    private static IReadOnlyList<Rectangle> KeyLayout(Rectangle body)
    {
        var keys = new List<Rectangle>();
        foreach (var y in new[] { body.Y + 12, body.Y + 25, body.Y + 38 })
            foreach (var x in new[] { body.X + 11, body.X + 25, body.X + 39, body.X + 53, body.X + 67, body.X + 81 })
                keys.Add(new Rectangle(x, y, 11, 9));
        return keys;
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
        var relevant = sessions.Where(session => session.IsAlive ||
            session.State == K15NormalizedState.DonePendingAttention).ToArray();
        var doneCandidates = relevant.Where(session =>
            session.State == K15NormalizedState.DonePendingAttention).ToArray();
        var canPollUnread = CodexPetAdapter.ShouldPollUnread(sessions);
        if (canPollUnread && !_refresh.Enabled) _refresh.Start();
        if (!canPollUnread && _refresh.Enabled) _refresh.Stop();
        var target = relevant.Length == 1 ? relevant[0].SessionId : null;
        var unreadState = canPollUnread && !string.IsNullOrWhiteSpace(doneCandidates[0].ThreadId)
            ? _reader.Read(DateTimeOffset.UtcNow).ForThread(doneCandidates[0].ThreadId)
            : CodexUnreadState.Unknown;
        var snapshot = CodexPetAdapter.Map(sessions, target, unreadState);
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
