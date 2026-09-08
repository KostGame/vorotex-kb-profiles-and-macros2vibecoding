using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>Deterministic, bounded v1 request/response protocol.</summary>
public static class RuntimeIpcProtocol
{
    private static readonly HashSet<string> AllowedRequestProperties = new(StringComparer.Ordinal)
    {
        "protocolVersion", "command"
    };

    public static string Handle(
        string json,
        Func<RuntimeSnapshot> snapshotProvider,
        Func<RuntimeIpcRuntimeHealth>? runtimeHealthProvider = null)
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
            if (properties.Any(property => !AllowedRequestProperties.Contains(property.Name))
                || properties.Length != properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count())
                return SerializeError("INVALID_REQUEST", "UNSUPPORTED_FIELD");
            if (!TryString(root, "protocolVersion", out var protocolVersion)
                || protocolVersion != RuntimeIpcMetadata.ProtocolVersion)
                return SerializeError("INVALID_PROTOCOL_VERSION", "UNKNOWN_VERSION");
            if (!TryString(root, "command", out var command))
                return SerializeError("INVALID_REQUEST", "MALFORMED");

            var response = command switch
            {
                "ping" => RuntimeContractJson.Serialize(new RuntimeIpcResponse(
                    RuntimeIpcMetadata.ProtocolVersion, true, "ping")),
                "snapshot" => RuntimeContractJson.Serialize(new RuntimeIpcResponse(
                    RuntimeIpcMetadata.ProtocolVersion, true, "snapshot",
                    RuntimeIpcSnapshot.From(snapshotProvider(), runtimeHealthProvider()))),
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
