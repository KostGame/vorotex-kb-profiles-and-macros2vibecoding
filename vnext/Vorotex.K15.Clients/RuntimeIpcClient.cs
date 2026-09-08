using System.IO.Pipes;
using System.Text;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Clients;

public sealed record RuntimeClientResult(bool Success, RuntimeIpcResponse? Response, string Error)
{
    public RuntimeIpcSnapshot? Snapshot => Response?.Snapshot;
    public static RuntimeClientResult Offline(string error) => new(false, null, error);
}

public interface IRuntimeIpcTransport
{
    Task<RuntimeClientResult> SendAsync(RuntimeIpcRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class NamedPipeRuntimeIpcTransport : IRuntimeIpcTransport
{
    private readonly string _pipeName;
    public NamedPipeRuntimeIpcTransport(string? pipeName = null) => _pipeName = pipeName ?? RuntimeContractMetadata.SingleInstanceName;

    public async Task<RuntimeClientResult> SendAsync(RuntimeIpcRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var json = RuntimeContractJson.Serialize(request);
            if (Encoding.UTF8.GetByteCount(json) + 1 > RuntimeIpcMetadata.MaxFrameBytes)
                return RuntimeClientResult.Offline("REQUEST_TOO_LARGE");
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            var response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response) || Encoding.UTF8.GetByteCount(response) > RuntimeIpcMetadata.MaxFrameBytes)
                return RuntimeClientResult.Offline("INVALID_RESPONSE");
            var parsed = RuntimeContractJson.Deserialize<RuntimeIpcResponse>(response);
            return parsed is null ? RuntimeClientResult.Offline("INVALID_RESPONSE") : new(parsed.Ok, parsed, parsed.Error ?? (parsed.Ok ? "" : "COMMAND_FAILED"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return RuntimeClientResult.Offline("TIMEOUT"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { return RuntimeClientResult.Offline("DISCONNECTED"); }
    }
}

public sealed class RuntimeIpcClient
{
    private readonly IRuntimeIpcTransport _transport;
    private readonly TimeSpan _timeout;
    public RuntimeIpcClient(IRuntimeIpcTransport? transport = null, TimeSpan? timeout = null)
    {
        _transport = transport ?? new NamedPipeRuntimeIpcTransport();
        _timeout = timeout ?? TimeSpan.FromMilliseconds(700);
    }

    public Task<RuntimeClientResult> RequestAsync(string command, string? candidateId = null, bool? enabled = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(new RuntimeIpcRequest(RuntimeIpcMetadata.ProtocolVersion, command, candidateId, enabled), _timeout, cancellationToken);
    public Task<RuntimeClientResult> SnapshotAsync(CancellationToken cancellationToken = default) => RequestAsync("snapshot", cancellationToken: cancellationToken);
    public Task<RuntimeClientResult> SendCommandAsync(string command, string? candidateId = null, bool? enabled = null, CancellationToken cancellationToken = default) => RequestAsync(command, candidateId, enabled, cancellationToken);
}
