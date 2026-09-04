using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.StatusLab;

public sealed record StatusTraySnapshot(
    string State,
    string Reason,
    DateTimeOffset StateSinceUtc,
    string Session,
    string Cwd,
    bool RgbEnabled,
    string RgbStatus,
    string NotificationStatus,
    bool DetailedLogging,
    bool HooksHealthy,
    string HooksStatus,
    string HooksDetail,
    bool Autostart,
    string ConfigPath,
    int ConfigSchema,
    string DeviceState = "DISCONNECTED",
    string DeviceIdentity = "",
    IReadOnlyList<StatusTrayDeviceCandidate>? DeviceCandidates = null,
    IReadOnlyList<StatusTraySessionSnapshot>? Sessions = null,
    int RunningCount = 0,
    int WaitingCount = 0,
    int DoneUnreadCount = 0,
    int ActiveTaskSessionCount = 0,
    int EndedSessionCount = 0);

public sealed record StatusTraySessionSnapshot(
    string SessionId,
    string State,
    bool IsAlive,
    bool IsFocused,
    string Cwd = "",
    string ThreadId = "",
    string TurnId = "",
    DateTimeOffset? LastActivityUtc = null);

public sealed record StatusTrayDeviceCandidate(
    string CandidateId,
    string ProductString,
    string VendorProduct,
    string Usage,
    int FeatureReportLength,
    bool? ProtocolVerified,
    string VerificationResult);

internal sealed record StatusTrayIpcRequest(string Command, string? Value = null);
internal sealed record StatusTrayIpcResponse(bool Success, string? Error = null, StatusTraySnapshot? Snapshot = null);

internal static class StatusTrayIpc
{
    public const string PipeName = "Vorotex.K15.StatusTray.v1";

    public static async Task RunServerAsync(
        Func<StatusTrayIpcRequest, Task<StatusTrayIpcResponse>> handler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                StatusTrayIpcResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<StatusTrayIpcRequest>(line)
                        ?? throw new InvalidDataException("IPC request is empty.");
                    response = await handler(request);
                }
                catch (Exception ex)
                {
                    response = new(false, ex.Message);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    public static async Task<StatusTrayIpcResponse> SendAsync(
        string command,
        string? value = null,
        int timeoutMs = 1800,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        await using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(new StatusTrayIpcRequest(command, value)));
        var line = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(line))
            return new(false, "Status Tray вернул пустой IPC-ответ.");

        return JsonSerializer.Deserialize<StatusTrayIpcResponse>(line)
            ?? new StatusTrayIpcResponse(false, "Status Tray вернул повреждённый IPC-ответ.");
    }
}
