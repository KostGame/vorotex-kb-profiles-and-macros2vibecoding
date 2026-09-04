using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.LiveDashboard;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("K15_LIVE_DASHBOARD_PORT"), out var parsed) ? parsed : 17815;
        if (!LoopbackPolicy.IsValidPort(port)) { FailureJournal.Write("bind_failed", "InvalidPort"); return; }
        using var mutex = new Mutex(true, "Local\\Vorotex.K15.LiveDashboard", out var created); if (!created) return;
        var tailer = new JournalTailer(); var poller = new SnapshotPoller(); var clients = new ConcurrentDictionary<Guid, SseClientBuffer>();
        var builder = WebApplication.CreateBuilder(args); builder.Logging.ClearProviders(); builder.Logging.AddConsole(); builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
        var app = builder.Build(); app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot")) }); app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot")) });
        app.MapGet("/health", () => Results.Json(new { ok = true, loopbackOnly = true, online = poller.Online }));
        app.MapGet("/api/snapshot", () => Results.Json(new { trayOnline = poller.Online, snapshot = poller.Snapshot }));
        app.MapGet("/api/events", (int? limit) => Results.Json(tailer.Snapshot().TakeLast(Math.Clamp(limit ?? 200, 1, 500))));
        app.MapGet("/api/stream", async (HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.ContentType = "text/event-stream"; var id = Guid.NewGuid();
            var channel = new SseClientBuffer(); clients[id] = channel;
            try { await foreach (var item in channel.Reader.ReadAllAsync(ct)) { await response.WriteAsync("data: " + JsonSerializer.Serialize(item) + "\n\n", ct); await response.Body.FlushAsync(ct); } }
            catch (OperationCanceledException) { } finally { clients.TryRemove(id, out _); }
        });
        FailureJournal.Write("started", null, port); var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(350));
        _ = Task.Run(async () => { while (await timer.WaitForNextTickAsync()) { await poller.PollAsync(); foreach (var item in tailer.Poll()) foreach (var client in clients.Values) client.TryWrite(item); } });
        try { await app.RunAsync($"http://127.0.0.1:{port}"); } catch (Exception ex) { FailureJournal.Write("runtime_error", ex.GetType().Name, null, ex.HResult); } finally { FailureJournal.Write("stopped"); }
    }
}

internal static class FailureJournal
{
    public static void Write(string name, string? category = null, int? port = null, int? hresult = null)
    {
        try { var value = new Dictionary<string, object?> { ["timestampUtc"] = DateTimeOffset.UtcNow, ["source"] = "live_dashboard", ["event"] = name }; if (name == "started") { value["version"] = "1.0.0"; value["loopbackPort"] = port; } else if (category is not null) { value["exceptionType"] = category; value["hresult"] = hresult ?? 0; value["category"] = name; } EventJournal.Append(value); } catch { }
    }
}
