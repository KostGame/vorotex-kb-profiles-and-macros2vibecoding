namespace Vorotex.K15.StatusLab;

internal sealed class CodexPetWindow : Form
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 180 };
    private CodexPetVisualState _state;
    private int _phase;

    public CodexPetWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(128, 128);
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 30);
        TransparencyKey = BackColor;
        DoubleBuffered = true;
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
        SetWindowPos(Handle, -1, Screen.PrimaryScreen?.WorkingArea.Right - Width - 24 ?? 0,
            Screen.PrimaryScreen?.WorkingArea.Bottom - Height - 24 ?? 0, Width, Height, 0x0010);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
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
        _normalizer.StateChanged += OnStateChanged;
        _refresh.Tick += (_, _) => Refresh();
        Refresh();
    }

    public bool Visible => _visible;
    public void Toggle()
    {
        _visible = !_visible;
        if (_visible) _window.Show(); else _window.Hide();
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
        var target = sessions.Count == 1 ? sessions[0].SessionId : null;
        var snapshot = CodexPetAdapter.Map(sessions, target, thread =>
            _reader.Read(DateTimeOffset.UtcNow).ForThread(thread));
        _window.SetState(snapshot.State);
        if (snapshot.State == CodexPetVisualState.Review && !_refresh.Enabled) _refresh.Start();
        if (snapshot.State != CodexPetVisualState.Review && _refresh.Enabled) _refresh.Stop();
    }

    public void Dispose()
    {
        _normalizer.StateChanged -= OnStateChanged;
        _refresh.Dispose();
        _window.Close();
        _window.Dispose();
    }
}
