using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.LiveDashboard;

internal sealed record SafeEvent(DateTimeOffset TimestampUtc, string Source, string Event, string SessionId = "", string TurnId = "", string State = "", string Previous = "", string Current = "", string Reason = "", string Status = "", string Decision = "");

internal static class EventSanitizer
{
    private static readonly HashSet<string> HookEvents = new(StringComparer.Ordinal) { "UserPromptSubmit", "PermissionRequest", "PreToolUse", "PostToolUse", "Stop", "SessionEnd" };
    private static readonly HashSet<string> BridgeEvents = new(StringComparer.Ordinal) { "approval_resolved", "turn_completed" };
    private static readonly HashSet<string> NormalizerEvents = new(StringComparer.Ordinal) { "normalized_state_changed", "session_state_changed", "state_rehydrated", "normalizer_error" };
    public static SafeEvent? Project(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            var source = String(r, "source"); var name = String(r, "event");
            if (!DateTimeOffset.TryParse(String(r, "timestampUtc"), out var timestamp)) return null;
            if (source == "codex_hook" && !HookEvents.Contains(name) || source == "codex_stdio_bridge" && !BridgeEvents.Contains(name) || source == "state_normalizer" && !NormalizerEvents.Contains(name) || source is not ("codex_hook" or "codex_stdio_bridge" or "state_normalizer" or "live_dashboard" or "status_tray")) return null;
            if (source == "live_dashboard" && name is not ("started" or "stopped" or "bind_failed" or "runtime_error")) return null;
            if (source == "status_tray" && name is not ("started" or "stopped")) return null;
            return new SafeEvent(timestamp.ToUniversalTime(), source, name, Opaque(r, "sessionId"), Opaque(r, "turnId"), Opaque(r, "state"), Opaque(r, "previous"), Opaque(r, "current"), SafeReason(r), SafeStatus(r), SafeDecision(r));
        }
        catch (JsonException) { return null; }
    }
    private static string String(JsonElement r, string n) => r.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    private static string Opaque(JsonElement r, string n) { var v = String(r, n); return v.Length <= 128 ? v : v[..128]; }
    private static string SafeReason(JsonElement r) => Opaque(r, "reason");
    private static string SafeStatus(JsonElement r) => Opaque(r, "status");
    private static string SafeDecision(JsonElement r) => Opaque(r, "decision");
}

internal sealed class JournalTailer
{
    private readonly ConcurrentQueue<SafeEvent> _events = new(); private readonly int _capacity; private long _offset; private string _remainder = ""; private DateTime _lastWrite;
    public JournalTailer(int capacity = 300) { _capacity = Math.Clamp(capacity, 200, 500); LoadArchive(); }
    private void LoadArchive()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VOROTEX", "K15 Status Lab", "events.jsonl.1");
        try { if (!File.Exists(path)) return; foreach (var line in File.ReadLines(path)) { var e=EventSanitizer.Project(line); if(e is not null) _events.Enqueue(e); } while(_events.Count>_capacity) _events.TryDequeue(out _); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public IReadOnlyList<SafeEvent> Snapshot() => _events.ToArray();
    public IReadOnlyList<SafeEvent> Poll()
    {
        var added = new List<SafeEvent>(); var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VOROTEX", "K15 Status Lab", "events.jsonl");
        try { if (!File.Exists(path)) return added; var info = new FileInfo(path); if (info.Length < _offset || info.LastWriteTimeUtc < _lastWrite) { _offset = 0; _remainder = ""; } _lastWrite = info.LastWriteTimeUtc; using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); s.Seek(_offset, SeekOrigin.Begin); using var reader = new StreamReader(s); var text = _remainder + reader.ReadToEnd(); _offset = s.Position; var lines = text.Split('\n'); for (var i=0;i<lines.Length-1;i++) { var e=EventSanitizer.Project(lines[i].TrimEnd('\r')); if(e is not null) { _events.Enqueue(e); added.Add(e); while(_events.Count > _capacity) _events.TryDequeue(out _); } } _remainder = text.EndsWith('\n') ? "" : lines[^1]; } catch (IOException) { } catch (UnauthorizedAccessException) { } return added;
    }
}

internal sealed class SnapshotPoller
{
    public StatusTraySnapshot? Snapshot { get; private set; }
    public bool Online { get; private set; }
    public async Task PollAsync() { try { var r=await StatusTrayIpc.SendAsync("snapshot", timeoutMs:700); Snapshot=r.Success?r.Snapshot:null; Online=r.Success && r.Snapshot is not null; } catch { Snapshot=null; Online=false; } }
}

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("K15_LIVE_DASHBOARD_PORT"), out var p) ? p : 17815;
        if (port is < 1024 or > 65535) throw new InvalidOperationException("K15_LIVE_DASHBOARD_PORT must be 1024..65535.");
        using var mutex = new Mutex(true, "Local\\Vorotex.K15.LiveDashboard", out var created); if (!created) return;
        var tailer = new JournalTailer(); var poller = new SnapshotPoller(); var clients = new ConcurrentDictionary<Guid, Channel<SafeEvent>>();
        var builder = WebApplication.CreateBuilder(args); builder.Logging.ClearProviders(); builder.Logging.AddConsole(); builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port)); var app=builder.Build(); app.UseDefaultFiles(); app.UseStaticFiles();
        app.MapGet("/health", () => Results.Json(new { ok=true, loopbackOnly=true, online=poller.Online }));
        app.MapGet("/api/snapshot", () => Results.Json(new { trayOnline=poller.Online, snapshot=poller.Snapshot }));
        app.MapGet("/api/events", (int? limit) => Results.Json(tailer.Snapshot().TakeLast(Math.Clamp(limit ?? 200, 1, 500))));
        app.MapGet("/api/stream", async (HttpResponse response, CancellationToken ct) => { response.Headers.ContentType="text/event-stream"; var id=Guid.NewGuid(); var ch=Channel.CreateUnbounded<SafeEvent>(); clients[id]=ch; try { await foreach(var e in ch.Reader.ReadAllAsync(ct)) { await response.WriteAsync("data: "+JsonSerializer.Serialize(e)+"\n\n",ct); await response.Body.FlushAsync(ct); } } catch(OperationCanceledException){} finally { clients.TryRemove(id,out _); } });
        EventJournalWriter.Started(port); var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(350)); _=Task.Run(async()=>{ while(await timer.WaitForNextTickAsync()) { await poller.PollAsync(); foreach(var e in tailer.Poll()) foreach(var c in clients.Values) c.Writer.TryWrite(e); } });
        try { await app.RunAsync("http://127.0.0.1:"+port); } finally { EventJournalWriter.Stopped(); }
    }
}

internal static class EventJournalWriter
{
    public static void Started(int port) { try { EventJournal.Append(new { timestampUtc=DateTimeOffset.UtcNow, source="live_dashboard", @event="started", version="1.0.0", loopbackPort=port }); } catch { } }
    public static void Stopped() { try { EventJournal.Append(new { timestampUtc=DateTimeOffset.UtcNow, source="live_dashboard", @event="stopped" }); } catch { } }
}
