namespace Vorotex.K15.VisualTestRig;

public interface ICameraFrameSource : IDisposable
{
    event EventHandler<Bitmap>? FrameAvailable;
    Task<IReadOnlyList<CameraDevice>> EnumerateAsync(CancellationToken cancellationToken);
    Task StartAsync(CameraDevice device, CancellationToken cancellationToken);
    Task StopAsync();
    bool IsRunning { get; }
    CameraDiagnostics Diagnostics { get; }
    void ReportUiDeliveryFailure(Exception error);
}
