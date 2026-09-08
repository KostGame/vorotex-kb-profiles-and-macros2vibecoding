namespace Vorotex.K15.Clients;

public sealed class RuntimeClientLoop
{
    private readonly RuntimeIpcClient _client;
    public RuntimeClientLoop(RuntimeIpcClient? client = null) => _client = client ?? new RuntimeIpcClient();
    public RuntimeIpcClient Client => _client;
    public async Task<RuntimeClientProjection> ReadAsync(bool includeDiagnostics = false, CancellationToken cancellationToken = default)
    {
        var result = await _client.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return RuntimeProjection.From(result.Snapshot, includeDiagnostics);
    }
}
