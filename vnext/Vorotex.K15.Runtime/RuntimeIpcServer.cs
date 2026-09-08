using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>Deterministic, bounded v1 request/response protocol.</summary>
public static class RuntimeIpcProtocol
{
    private static readonly HashSet<string> CommonFields = new(StringComparer.Ordinal) { "protocolVersion", "command" };
    private static readonly HashSet<string> CandidateFields = new(StringComparer.Ordinal) { "protocolVersion", "command", "candidateId" };
    private static readonly HashSet<string> EnabledFields = new(StringComparer.Ordinal) { "protocolVersion", "command", "enabled" };

    public sealed record RuntimeIpcCommandResult(bool Ok, string? Error);

    public static string Handle(
        string json,
        Func<RuntimeSnapshot> snapshotProvider,
        Func<RuntimeIpcRuntimeHealth>? runtimeHealthProvider = null,
        Func<string, string?, bool?, RuntimeIpcCommandResult>? commandHandler = null,
        Func<RuntimeIpcDeviceSnapshot>? deviceProvider = null,
        Func<RuntimeIpcRgbSnapshot>? rgbProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        runtimeHealthProvider ??= () => new RuntimeIpcRuntimeHealth(
            RuntimeContractMetadata.CurrentRuntimeVersion, false, "UNAVAILABLE");
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > RuntimeIpcMetadata.MaxFrameBytes)
            return SerializeError("INVALID_REQUEST", "MALFORMED");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SerializeError("INVALID_REQUEST", "MALFORMED");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                || properties.Any(property => property.Name is not ("protocolVersion" or "command" or "candidateId" or "enabled")))
                return SerializeError("INVALID_REQUEST", "UNSUPPORTED_FIELD");
            if (!TryString(root, "protocolVersion", out var protocolVersion)
                || protocolVersion != RuntimeIpcMetadata.ProtocolVersion)
                return SerializeError("INVALID_PROTOCOL_VERSION", "UNKNOWN_VERSION");
            if (!TryString(root, "command", out var commandValue))
                return SerializeError("INVALID_REQUEST", "MALFORMED");
            var command = commandValue!;

            var allowed = command == "connect_device" ? CandidateFields : command == "set_rgb_enabled" ? EnabledFields : CommonFields;
            if (properties.Any(property => !allowed.Contains(property.Name))) return SerializeError(command, "UNSUPPORTED_FIELD");
            string? candidateId = null;
            bool? enabled = null;
            if (root.TryGetProperty("candidateId", out var candidateElement))
            {
                candidateId = candidateElement.ValueKind == JsonValueKind.String ? candidateElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(candidateId) || candidateId.Length > 32) return SerializeError(command, "INVALID_ARGUMENT");
            }
            if (root.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False)
                return SerializeError(command, "INVALID_ARGUMENT");
            if (root.TryGetProperty("enabled", out enabledElement)) enabled = enabledElement.GetBoolean();

            var response = command switch
            {
                "ping" => RuntimeContractJson.Serialize(new RuntimeIpcResponse(
                    RuntimeIpcMetadata.ProtocolVersion, true, "ping")),
                "snapshot" => RuntimeContractJson.Serialize(new RuntimeIpcResponse(RuntimeIpcMetadata.ProtocolVersion, true, "snapshot", RuntimeIpcSnapshot.From(snapshotProvider(), runtimeHealthProvider(), deviceProvider?.Invoke(), rgbProvider?.Invoke()))),
                "scan_devices" or "connect_device" or "disconnect_device" or "reconnect_device" or "set_rgb_enabled" or "restore_lighting" => HandleMutation(command, candidateId, enabled, snapshotProvider, runtimeHealthProvider, commandHandler, deviceProvider, rgbProvider),
                "acknowledge_attention" or "shutdown_runtime" => SerializeError(command, "UNSUPPORTED_COMMAND"),
                _ => SerializeError("INVALID_COMMAND", "UNKNOWN_COMMAND")
            };
            // The newline delimiter is part of the wire frame.
            if (Encoding.UTF8.GetByteCount(response) + 1 <= RuntimeIpcMetadata.MaxFrameBytes)
                return response;
            return SerializeError("snapshot", "SNAPSHOT_TOO_LARGE");
        }
        catch (JsonException)
        {
            return SerializeError("INVALID_REQUEST", "MALFORMED");
        }
    }

    private static string HandleMutation(string command, string? candidateId, bool? enabled, Func<RuntimeSnapshot> snapshotProvider,
        Func<RuntimeIpcRuntimeHealth> healthProvider, Func<string, string?, bool?, RuntimeIpcCommandResult>? handler,
        Func<RuntimeIpcDeviceSnapshot>? deviceProvider, Func<RuntimeIpcRgbSnapshot>? rgbProvider)
    {
        if (handler is null) return SerializeError(command, "UNAVAILABLE");
        var result = handler(command, candidateId, enabled);
        return result.Ok ? RuntimeContractJson.Serialize(new RuntimeIpcResponse(RuntimeIpcMetadata.ProtocolVersion, true, command, RuntimeIpcSnapshot.From(snapshotProvider(), healthProvider(), deviceProvider?.Invoke(), rgbProvider?.Invoke()))) : SerializeError(command, result.Error ?? "COMMAND_FAILED");
    }

    private static string SerializeError(string command, string error) =>
        RuntimeContractJson.Serialize(new RuntimeIpcResponse(
            RuntimeIpcMetadata.ProtocolVersion, false, command, Error: error));

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        value = null;
        return false;
    }
}

/// <summary>Current-user-only newline-framed named-pipe server. It owns no state.</summary>
public sealed class RuntimeIpcServer : IAsyncDisposable
{
    private readonly RuntimeHost _host;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _listenerGate = new();
    private NamedPipeServerStream? _listener;
    private Task? _acceptLoop;

    public RuntimeIpcServer(RuntimeHost host, string? pipeName = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pipeName = pipeName ?? RuntimeContractMetadata.SingleInstanceName;
    }

    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("IPC server already started.");
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (_listenerGate) _listener = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    _ = HandleClientAsync(pipe);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    lock (_listenerGate)
                    {
                        if (ReferenceEquals(_listener, pipe)) _listener = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        await using var ownedPipe = pipe;
        await using var writer = new StreamWriter(ownedPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var request = await ReadBoundedLineAsync(ownedPipe, _stop.Token).ConfigureAwait(false);
        await writer.WriteLineAsync(_host.HandleIpcRequest(request)).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedLineAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(RuntimeIpcMetadata.MaxFrameBytes + 1);
        var buffer = new byte[1024];
        while (true)
        {
            var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(buffer[index]);
                if (bytes.Count > RuntimeIpcMetadata.MaxFrameBytes)
                    return new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_listenerGate) _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
