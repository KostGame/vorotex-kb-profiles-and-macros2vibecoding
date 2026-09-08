using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>
/// Dedicated bridge ingress. It is intentionally separate from RuntimeIpcServer:
/// the pipe accepts only sanitized native-authority records and has no command
/// or response surface.
/// </summary>
public sealed class NativeAuthorityIngressServer : IAsyncDisposable
{
    private readonly RuntimeHost _host;
    private readonly string _pipeName;
    private readonly NativeStatusDeliveryQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _listenerGate = new();
    private NamedPipeServerStream? _listener;
    private Task? _acceptLoop;

    public NativeAuthorityIngressServer(RuntimeHost host, string? pipeName = null, int queueCapacity = 64)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _pipeName = pipeName ?? RuntimeIpcMetadata.NativeAuthorityPipeName;
        _queue = new NativeStatusDeliveryQueue(_host.NativeStatusTransport, queueCapacity);
    }

    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("authority ingress already started");
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Exactly one server instance gives the first connected wrapper
                // deterministic producer ownership. Other wrappers see busy and
                // remain fail-open for the transparent Codex transport.
                var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (_listenerGate) _listener = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    await HandleProducerAsync(pipe).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (!_stop.IsCancellationRequested) { }
                finally
                {
                    lock (_listenerGate)
                    {
                        if (ReferenceEquals(_listener, pipe)) _listener = null;
                    }
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    if (!_stop.IsCancellationRequested) _host.MarkNativeAuthorityUnavailable();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleProducerAsync(NamedPipeServerStream pipe)
    {
        _host.MarkNativeAuthorityAvailable();
        while (!_stop.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await ReadBoundedLineAsync(pipe, _stop.Token).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0 || !_queue.TryEnqueue(line))
            {
                // Invalid/oversized input is rejected without affecting the
                // Runtime/UI command pipe or transparent Codex transport.
                if (line.Length > RuntimeIpcMetadata.MaxFrameBytes) _host.MarkNativeAuthorityUnavailable();
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(RuntimeIpcMetadata.MaxFrameBytes + 1);
        var buffer = new byte[1024];
        while (true)
        {
            var read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : string.Empty;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(buffer[index]);
                if (bytes.Count > RuntimeIpcMetadata.MaxFrameBytes)
                {
                    while (index + 1 < read && buffer[index + 1] != (byte)'\n') index++;
                    return new string('x', RuntimeIpcMetadata.MaxFrameBytes + 1);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_listenerGate) _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await _queue.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
