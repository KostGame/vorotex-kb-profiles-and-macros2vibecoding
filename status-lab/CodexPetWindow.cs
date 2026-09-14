namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetWindow : Form
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 180 };
    private readonly CodexPetPositionStore _positionStore;
    private CodexPetVisualState _state;
    private int _phase;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    internal event Action? CloseRequested;

    public CodexPetWindow(CodexPetPositionStore? positionStore = null)
    {
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bob = _state == CodexPetVisualState.Idle ? (_phase % 4 == 0 ? 1 : 0) : (_phase % 2);
        var body = new Rectangle(18, 20 + bob, 92, 88);
        var color = _state switch
        {
            CodexPetVisualState.Running => Color.FromArgb(90, 190, 255),
            CodexPetVisualState.Waiting => Color.FromArgb(255, 196, 80),
            CodexPetVisualState.Review => Color.FromArgb(190, 130, 255),
            _ => Color.FromArgb(100, 220, 170)
        };
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(35, 35, 45), 4);
        g.FillEllipse(fill, body);
        g.DrawEllipse(outline, body);
        using var eye = new SolidBrush(Color.FromArgb(30, 35, 45));
        g.FillEllipse(eye, 42, 52 + bob, 12, 16);
        g.FillEllipse(eye, 74, 52 + bob, 12, 16);
        using var mouth = new Pen(Color.FromArgb(30, 35, 45), 4);
        var smile = _state == CodexPetVisualState.Waiting ? new Point[] { new(52, 82 + bob), new(64, 76 + bob), new(76, 82 + bob) } :
            new Point[] { new(52, 78 + bob), new(64, 86 + bob), new(76, 78 + bob) };
        g.DrawLines(mouth, smile);
        if (_state == CodexPetVisualState.Running)
            g.DrawArc(new Pen(Color.FromArgb(90, 190, 255), 3), 8, 36, 112, 72, 20, 140);
        if (_state == CodexPetVisualState.Review)
            g.DrawArc(new Pen(Color.FromArgb(255, 230, 120), 4), 42, 8, 44, 28, 190, 160);
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
    private readonly CodexPetWindow _window = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private bool _visible;

    public CodexPetController(JournalStateNormalizer normalizer, ICodexUnreadStateReader reader)
    {
        _normalizer = normalizer;
        _reader = reader;
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
