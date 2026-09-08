using System.Text.Json;
using Vorotex.K15.Clients;

namespace Vorotex.K15.LiveDashboard;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("K15_VNEXT_DASHBOARD_PORT"), out var configured) && configured is >= 1024 and <= 65535 ? configured : 17816;
        var builder = WebApplication.CreateBuilder(args); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
        var app = builder.Build(); app.UseDefaultFiles(); app.UseStaticFiles(); var runtime = new RuntimeClientLoop();
        app.MapGet("/health", async () => Results.Json(new { ok = true, loopbackOnly = true, runtime = (await runtime.ReadAsync()).Health }));
        app.MapGet("/api/snapshot", async (bool? diagnostics, CancellationToken ct) => Results.Json(await runtime.ReadAsync(diagnostics == true, ct), new JsonSerializerOptions { WriteIndented = false }));
        await app.RunAsync($"http://127.0.0.1:{port}");
    }
}
