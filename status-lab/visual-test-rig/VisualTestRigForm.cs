namespace Vorotex.K15.VisualTestRig;

public sealed class VisualTestRigForm : Form
{
    private readonly ICameraFrameSource _camera; private readonly CaptureStorage _storage; private readonly SettingsStore _settings;
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Button _refresh = new() { Text = "Refresh cameras" }, _start = new() { Text = "Start Camera" }, _stop = new() { Text = "Stop Camera", Enabled = false };
    private readonly Button _capture = new() { Text = "Capture", Enabled = false }, _burst = new() { Text = "Capture Burst", Enabled = false }, _clearRoi = new() { Text = "Clear ROI" };
    private readonly NumericUpDown _seconds = new() { Minimum = BurstPlan.MinSeconds, Maximum = BurstPlan.MaxSeconds, Value = 5, Width = 55 };
    private readonly NumericUpDown _fps = new() { Minimum = BurstPlan.MinFramesPerSecond, Maximum = BurstPlan.MaxFramesPerSecond, Value = 5, Width = 55 };
    private readonly PreviewBox _preview = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label _cameraState = Status("CAMERA OFF", Color.Firebrick, 18), _timestamp = Status("Timestamp: —"), _details = Status("Selected camera: —\r\nPreview: —\r\nROI: disabled\r\nLatest capture: —\r\nBurst: idle\r\nLast error: —");
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 250 };
    private readonly object _frameGate = new(); private Bitmap? _latestFrame; private Bitmap? _pendingPreviewFrame; private int _previewDeliveryQueued;
    private CameraDevice? _selected; private bool _burstActive; private bool _disposed; private DateTimeOffset? _latestCaptureUtc;

    public VisualTestRigForm(ICameraFrameSource camera, CaptureStorage storage)
    {
        _camera = camera; _storage = storage; _settings = new SettingsStore(AppPaths.DataRoot); _preview.Roi = _settings.Load().Roi;
        Text = "K15 Visual Test Rig · v0"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(1100, 720); Size = new Size(1300, 850); BackColor = Color.WhiteSmoke;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 86, Padding = new Padding(12), BackColor = Color.White, AutoSize = false };
        controls.Controls.AddRange([new Label { Text = "Camera", AutoSize = true, Margin = new Padding(3, 12, 3, 3) }, _devices, _refresh, _start, _stop, _capture, _burst, new Label { Text = "Burst: sec", AutoSize = true, Margin = new Padding(10, 12, 2, 3) }, _seconds, new Label { Text = "fps", AutoSize = true, Margin = new Padding(6, 12, 2, 3) }, _fps, _clearRoi]);
        var status = new Panel { Dock = DockStyle.Bottom, Height = 240, Padding = new Padding(14), BackColor = Color.White };
        _cameraState.Location = new Point(14, 12); _timestamp.Location = new Point(14, 46); _details.Location = new Point(14, 74); _details.AutoSize = true;
        status.Controls.AddRange([_cameraState, _timestamp, _details]); Controls.AddRange([_preview, status, controls]);
        _refresh.Click += async (_, _) => await RefreshDevicesAsync(); _start.Click += async (_, _) => await StartAsync(); _stop.Click += async (_, _) => await StopAsync(); _capture.Click += async (_, _) => await CaptureAsync(); _burst.Click += async (_, _) => await BurstAsync();
        _clearRoi.Click += (_, _) => { _preview.Roi = NormalizedRoi.Disabled; PersistRoi(); RefreshDetails(); };
        _preview.RoiChanged += (_, _) => { PersistRoi(); RefreshDetails(); }; _camera.FrameAvailable += CameraOnFrameAvailable;
        _clock.Tick += (_, _) => { _timestamp.Text = "Timestamp: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"); UpdateCameraState(); RefreshDetails(); }; _clock.Start();
        Shown += async (_, _) => await RefreshDevicesAsync();
    }
    private static Label Status(string text, Color? color = null, int fontSize = 10) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, fontSize, FontStyle.Bold), ForeColor = color ?? Color.Black };
    private async Task RefreshDevicesAsync()
    {
        try { var list = await _camera.EnumerateAsync(CancellationToken.None); _devices.DataSource = list; _devices.DisplayMember = nameof(CameraDevice.Name); _devices.ValueMember = nameof(CameraDevice.Id); SetError(list.Count == 0 ? "No video capture devices found." : "—"); }
        catch (Exception ex) { SetError("Camera enumeration unavailable: " + ex.Message); }
    }
    private async Task StartAsync()
    {
        try { _selected = _devices.SelectedItem as CameraDevice ?? throw new InvalidOperationException("Select a camera first."); await _camera.StartAsync(_selected, CancellationToken.None); SetCameraState("CAMERA STARTED", Color.DarkOrange); _start.Enabled = false; _stop.Enabled = _capture.Enabled = _burst.Enabled = true; SetError("—"); }
        catch (Exception ex) { SetCameraState("CAMERA ERROR", Color.Firebrick); SetError("Camera access unavailable or denied: " + ex.Message); }
    }
    private async Task StopAsync()
    {
        try { await _camera.StopAsync(); } catch (Exception ex) { SetError("Camera stop error: " + ex.Message); }
        SetCameraState("CAMERA OFF", Color.Firebrick); _start.Enabled = true; _stop.Enabled = _capture.Enabled = _burst.Enabled = false;
        lock (_frameGate) { _latestFrame?.Dispose(); _latestFrame = null; _pendingPreviewFrame?.Dispose(); _pendingPreviewFrame = null; } _preview.SetFrame(null);
    }
    private void CameraOnFrameAvailable(object? sender, Bitmap frame)
    {
        if (IsDisposed || Disposing || _disposed) { frame.Dispose(); return; }
        Bitmap? replaced;
        lock (_frameGate) { replaced = _pendingPreviewFrame; _pendingPreviewFrame = frame; }
        replaced?.Dispose(); QueuePreviewDelivery();
    }
    private void QueuePreviewDelivery()
    {
        if (Interlocked.Exchange(ref _previewDeliveryQueued, 1) != 0) return;
        try { BeginInvoke(new Action(DeliverPendingPreview)); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _previewDeliveryQueued, 0); Bitmap? dropped;
            lock (_frameGate) { dropped = _pendingPreviewFrame; _pendingPreviewFrame = null; }
            dropped?.Dispose(); _camera.ReportUiDeliveryFailure(ex); SetError("Preview delivery failed: " + ex.Message);
        }
    }
    private void DeliverPendingPreview()
    {
        Bitmap? frame; lock (_frameGate) { frame = _pendingPreviewFrame; _pendingPreviewFrame = null; }
        try
        {
            if (frame is not null)
            {
                var previewCopy = (Bitmap)frame.Clone();
                lock (_frameGate) { _latestFrame?.Dispose(); _latestFrame = frame; frame = null; }
                _preview.SetFrame(previewCopy);
            }
        }
        catch (Exception ex) { frame?.Dispose(); _camera.ReportUiDeliveryFailure(ex); SetError("Preview delivery failed: " + ex.Message); }
        finally
        {
            Interlocked.Exchange(ref _previewDeliveryQueued, 0); bool hasPending;
            lock (_frameGate) hasPending = _pendingPreviewFrame is not null;
            if (hasPending) QueuePreviewDelivery();
        }
    }
    private async Task CaptureAsync()
    {
        try { SaveCapture("single", [SnapshotForCapture()]); await Task.CompletedTask; } catch (Exception ex) { SetError("Capture failed: " + ex.Message); }
    }
    private async Task BurstAsync()
    {
        if (_burstActive) return;
        var frames = new List<Bitmap>();
        try
        {
            _burstActive = true; _burst.Enabled = _capture.Enabled = false; RefreshDetails(); var plan = BurstPlan.Create((int)_seconds.Value, (int)_fps.Value);
            var started = DateTimeOffset.UtcNow;
            foreach (var at in plan) { var delay = started + at - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay); frames.Add(SnapshotForCapture()); }
            SaveCapture("burst", frames);
        }
        catch (Exception ex) { SetError("Burst failed: " + ex.Message); }
        finally { foreach (var frame in frames) frame.Dispose(); _burstActive = false; _burst.Enabled = _capture.Enabled = _camera.IsRunning; RefreshDetails(); }
    }
    private Bitmap SnapshotForCapture()
    {
        lock (_frameGate)
        {
            if (_latestFrame is null) throw new InvalidOperationException("No webcam frame is available yet.");
            var full = (Bitmap)_latestFrame.Clone(); var pixels = _preview.Roi.ToPixels(full.Size);
            if (pixels.IsEmpty) return full; var crop = full.Clone(pixels, full.PixelFormat); full.Dispose(); return crop;
        }
    }
    private void SaveCapture(string type, IReadOnlyList<Bitmap> frames)
    {
        try { var metadata = _storage.Save(type, _selected?.Name ?? "Unknown camera", frames, _preview.Roi); _latestCaptureUtc = metadata.CapturedUtc; SetError("—"); }
        finally { if (type == "single") foreach (var frame in frames) frame.Dispose(); }
    }
    private void PersistRoi() => _settings.Save(new RigSettings(1, _preview.Roi));
    private void RefreshDetails()
    {
        var diagnostics = _camera.Diagnostics; string preview;
        lock (_frameGate) preview = _latestFrame is null ? "—" : $"{_latestFrame.Width}×{_latestFrame.Height}";
        var source = diagnostics.SourceKind is null ? "—" : $"{diagnostics.SourceKind} / {diagnostics.NativeSubtype ?? "—"} / {diagnostics.Width?.ToString() ?? "—"}×{diagnostics.Height?.ToString() ?? "—"}{(diagnostics.FrameRate is null ? string.Empty : " / " + diagnostics.FrameRate)}";
        _details.Text = $"Camera: {diagnostics.CameraName}\r\nReader start: {diagnostics.ReaderStartStatus ?? "—"}\r\nSource: {source}\r\nLast error: {diagnostics.LastError ?? _lastError}\r\nFrame callbacks: {diagnostics.FrameArrivedCallbacks}; acquired: {diagnostics.FramesAcquired}; bitmaps: {diagnostics.BitmapsProduced}\r\nSoftwareBitmap frames: {diagnostics.SoftwareBitmapFrames}; Direct3D surface frames: {diagnostics.Direct3DSurfaceFrames}\r\nSurface copies: {diagnostics.SurfaceCopiesSucceeded} succeeded, {diagnostics.SurfaceCopiesFailed} failed; dropped: {diagnostics.FramesDroppedWhileProcessing}\r\nLast frame: {diagnostics.LastFrameUtc?.ToString("O") ?? "—"}\r\nPreview: {preview}; ROI: {DescribeRoi()}\r\nLatest capture: {_latestCaptureUtc?.ToString("O") ?? "—"}\r\nBurst: {(_burstActive ? "ACTIVE" : "idle")}\r\nLatest path: {_storage.LatestCapturePath ?? "—"}";
    }
    private string _lastError = "—"; private void SetError(string text) { _lastError = text; RefreshDetails(); }
    private string DescribeRoi() { var r = _preview.Roi.Clamp(); return r.Enabled ? $"enabled ({r.X:F3}, {r.Y:F3}, {r.Width:F3}, {r.Height:F3})" : "disabled (Capture saves full webcam frame)"; }
    private void UpdateCameraState()
    {
        if (!_camera.IsRunning) return;
        SetCameraState(_camera.Diagnostics.BitmapsProduced > 0 ? "CAMERA LIVE" : "CAMERA STARTED", _camera.Diagnostics.BitmapsProduced > 0 ? Color.ForestGreen : Color.DarkOrange);
    }
    private void SetCameraState(string text, Color color) { _cameraState.Text = text; _cameraState.ForeColor = color; }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _clock.Stop(); _camera.FrameAvailable -= CameraOnFrameAvailable;
            lock (_frameGate) { _latestFrame?.Dispose(); _latestFrame = null; _pendingPreviewFrame?.Dispose(); _pendingPreviewFrame = null; }
            _camera.Dispose();
        }
        base.Dispose(disposing);
    }
}

public sealed class PreviewBox : Control
{
    private Bitmap? _frame; private Point? _dragStart; private NormalizedRoi _roi = NormalizedRoi.Disabled;
    public NormalizedRoi Roi { get => _roi; set { _roi = value.Clamp(); Invalidate(); } } public event EventHandler? RoiChanged;
    public PreviewBox() { DoubleBuffered = true; }
    public void SetFrame(Bitmap? frame) { var old = _frame; _frame = frame; old?.Dispose(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (_frame is null) { TextRenderer.DrawText(e.Graphics, "Camera preview will appear here", Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        var destination = Fit(_frame.Size, ClientRectangle); e.Graphics.DrawImage(_frame, destination); var r = Roi.Clamp(); if (r.Enabled) { var roi = new Rectangle((int)(destination.Left + destination.Width * r.X), (int)(destination.Top + destination.Height * r.Y), (int)(destination.Width * r.Width), (int)(destination.Height * r.Height)); using var pen = new Pen(Color.Lime, 4); e.Graphics.DrawRectangle(pen, roi); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (_frame is not null && e.Button == MouseButtons.Left) _dragStart = e.Location; }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_frame is null || _dragStart is null) return; var bounds = Fit(_frame.Size, ClientRectangle); var a = ClampTo(e.Location, bounds); var b = ClampTo(_dragStart.Value, bounds); _dragStart = null;
        Roi = new NormalizedRoi(true, (double)Math.Min(a.X, b.X) / bounds.Width, (double)Math.Min(a.Y, b.Y) / bounds.Height, (double)Math.Abs(a.X - b.X) / bounds.Width, (double)Math.Abs(a.Y - b.Y) / bounds.Height).Clamp(); RoiChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }
    private static Point ClampTo(Point point, Rectangle bounds) => new(Math.Clamp(point.X - bounds.Left, 0, bounds.Width), Math.Clamp(point.Y - bounds.Top, 0, bounds.Height));
    private static Rectangle Fit(Size image, Rectangle area) { var scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height); var size = new Size((int)(image.Width * scale), (int)(image.Height * scale)); return new Rectangle((area.Width - size.Width) / 2, (area.Height - size.Height) / 2, size.Width, size.Height); }
    protected override void Dispose(bool disposing) { if (disposing) _frame?.Dispose(); base.Dispose(disposing); }
}
