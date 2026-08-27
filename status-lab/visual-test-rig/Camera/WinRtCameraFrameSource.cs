using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Storage.Streams;

namespace Vorotex.K15.VisualTestRig;

public sealed class WinRtCameraFrameSource : ICameraFrameSource
{
    private MediaCapture? _capture; private MediaFrameReader? _reader;
    private readonly object _diagnosticsGate = new(); private CameraDiagnostics _diagnostics = CameraDiagnostics.Empty;
    private readonly object _processingGate = new(); private bool _frameProcessing; private bool _stopping; private Task? _activeFrameProcessing;
    public event EventHandler<Bitmap>? FrameAvailable;
    public bool IsRunning => _reader is not null;
    public CameraDiagnostics Diagnostics { get { lock (_diagnosticsGate) return _diagnostics; } }
    public async Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
        return devices.Select(d => new CameraDevice(d.Id, d.Name)).ToArray();
    }
    public async Task StartAsync(CameraDevice device, CancellationToken cancellationToken)
    {
        await StopAsync();
        UpdateDiagnostics(_ => CameraDiagnostics.Empty with { CameraName = device.Name, CameraDeviceId = device.Id });
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings { VideoDeviceId = device.Id, StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu }).AsTask(cancellationToken);
            var source = _capture.FrameSources.Values.FirstOrDefault(x => x.Info.SourceKind == MediaFrameSourceKind.Color) ?? throw new InvalidOperationException("Camera has no color frame source.");
            var format = source.CurrentFormat;
            UpdateDiagnostics(d => d with
            {
                FrameSourceId = source.Info.Id, SourceKind = source.Info.SourceKind.ToString(), StreamType = source.Info.MediaStreamType.ToString(),
                NativeSubtype = format?.Subtype, Width = format is null ? null : format.VideoFormat.Width, Height = format is null ? null : format.VideoFormat.Height,
                FrameRate = DescribeFrameRate(format)
            });
            _reader = await _capture.CreateFrameReaderAsync(source).AsTask(cancellationToken);
            _reader.FrameArrived += OnFrameArrived;
            var status = await _reader.StartAsync().AsTask(cancellationToken);
            UpdateDiagnostics(d => d with { ReaderStartStatus = status.ToString() });
            if (status != MediaFrameReaderStartStatus.Success) { await StopAsync(); throw new InvalidOperationException($"Camera reader failed: {status}."); }
        }
        catch (Exception ex) { RecordError("Start", ex); await StopAsync(); throw; }
    }
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        UpdateDiagnostics(d => d with { FrameArrivedCallbacks = d.FrameArrivedCallbacks + 1 });
        Task? processing = null;
        lock (_processingGate)
        {
            if (!_stopping && !_frameProcessing)
            {
                _frameProcessing = true;
                processing = ProcessFrameAsync(sender);
                _activeFrameProcessing = processing;
            }
        }
        if (processing is null) UpdateDiagnostics(d => d with { FramesDroppedWhileProcessing = d.FramesDroppedWhileProcessing + 1 });
    }
    private async Task ProcessFrameAsync(MediaFrameReader sender)
    {
        var stage = "AcquireLatestFrame";
        try
        {
            using var frame = sender.TryAcquireLatestFrame(); if (frame is null) { RecordStage("AcquireLatestFrame", "No frame was available."); return; }
            UpdateDiagnostics(d => d with { FramesAcquired = d.FramesAcquired + 1 });
            stage = "VideoMediaFrame";
            var video = frame.VideoMediaFrame; if (video is null) { RecordStage(stage, "Frame did not expose VideoMediaFrame."); return; }
            SoftwareBitmap? surfaceCopy = null;
            var software = video.SoftwareBitmap;
            if (software is not null) UpdateDiagnostics(d => d with { SoftwareBitmapFrames = d.SoftwareBitmapFrames + 1 });
            else if (video.Direct3DSurface is not null)
            {
                UpdateDiagnostics(d => d with { Direct3DSurfaceFrames = d.Direct3DSurfaceFrames + 1 });
                stage = "Direct3DSurface copy";
                try
                {
                    surfaceCopy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(video.Direct3DSurface).AsTask();
                    software = surfaceCopy;
                    UpdateDiagnostics(d => d with { SurfaceCopiesSucceeded = d.SurfaceCopiesSucceeded + 1 });
                }
                catch
                {
                    UpdateDiagnostics(d => d with { SurfaceCopiesFailed = d.SurfaceCopiesFailed + 1 });
                    throw;
                }
            }
            else { RecordStage("VideoMediaFrame", "Frame has neither SoftwareBitmap nor Direct3DSurface."); return; }
            if (software is null) { RecordStage(stage, "Surface copy completed without a SoftwareBitmap."); return; }
            SoftwareBitmap? converted = null;
            try
            {
                var compatible = software.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && software.BitmapAlphaMode == BitmapAlphaMode.Premultiplied;
                stage = "SoftwareBitmap.Convert";
                var readable = compatible ? software : converted = SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var output = CopyToOwnedBitmap(readable, ref stage);
                var handler = FrameAvailable;
                if (handler is null) { output.Dispose(); RecordStage("UI event delivery", "No preview subscriber is attached."); return; }
                try { handler.Invoke(this, output); }
                catch (Exception ex) { output.Dispose(); RecordError("UI event delivery", ex); return; }
                UpdateDiagnostics(d => d with { BitmapsProduced = d.BitmapsProduced + 1, LastFrameUtc = DateTimeOffset.UtcNow, LastError = null });
            }
            finally { converted?.Dispose(); surfaceCopy?.Dispose(); }
        }
        catch (Exception ex) { RecordError(stage, ex); }
        finally { lock (_processingGate) { _frameProcessing = false; _activeFrameProcessing = null; } }
    }
    public async Task StopAsync()
    {
        Task? processing;
        lock (_processingGate) { _stopping = true; processing = _activeFrameProcessing; }
        try
        {
            if (_reader is not null) _reader.FrameArrived -= OnFrameArrived;
            if (processing is not null) await processing;
            if (_reader is not null) { await _reader.StopAsync().AsTask(); _reader.Dispose(); _reader = null; }
            _capture?.Dispose(); _capture = null;
        }
        finally { lock (_processingGate) _stopping = false; }
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
    public void ReportUiDeliveryFailure(Exception error) => RecordError("UI delivery", error);
    private void UpdateDiagnostics(Func<CameraDiagnostics, CameraDiagnostics> update) { lock (_diagnosticsGate) _diagnostics = update(_diagnostics); }
    private void RecordError(string stage, Exception error) => RecordStage(stage, $"{error.GetType().Name}: {error.Message}");
    private void RecordStage(string stage, string message) => UpdateDiagnostics(d => d with { LastError = $"{stage}: {message}" });
    private static Bitmap CopyToOwnedBitmap(SoftwareBitmap bitmap, ref string stage)
    {
        var width = checked((int)bitmap.PixelWidth); var height = checked((int)bitmap.PixelHeight); var byteCount = checked(checked(width * height) * 4);
        stage = "SoftwareBitmap.CopyToBuffer";
        var copied = new Windows.Storage.Streams.Buffer(checked((uint)byteCount)); bitmap.CopyToBuffer(copied);
        if (copied.Length < (uint)byteCount) throw new InvalidOperationException($"Copied buffer has {copied.Length} bytes; expected {byteCount}.");
        stage = "IBuffer read";
        var pixels = new byte[byteCount]; using (var reader = DataReader.FromBuffer(copied)) reader.ReadBytes(pixels);
        stage = "Bitmap managed copy";
        return ManagedBgraBitmap.Create(pixels, width, height);
    }
    private static string? DescribeFrameRate(MediaFrameFormat? format)
    {
        if (format is null) return null;
        var rate = format.FrameRate;
        return rate.Denominator == 0 ? null : $"{(double)rate.Numerator / rate.Denominator:F2} fps";
    }
}
