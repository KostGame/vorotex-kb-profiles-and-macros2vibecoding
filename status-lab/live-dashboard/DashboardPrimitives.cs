using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.LiveDashboard;

public sealed record SafeEvent(DateTimeOffset TimestampUtc, string Source, string Event, string SessionId = "", string TurnId = "", string State = "", string Previous = "", string Current = "", string Reason = "", string Status = "", string Decision = "");

public static class LoopbackPolicy
{
    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool IsLoopback(string host) => host is "127.0.0.1" or "::1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}

public static class EventSanitizer
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal) { "NORMAL", "RUNNING", "WAITING", "DONE_PENDING_ATTENTION", "ERROR", "ENDED" };
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) { "codex_read_ack", "codex_user_prompt_submit", "codex_permission_request", "codex_pre_tool_use", "codex_post_tool_use", "codex_stop", "codex_session_end", "codex_approval_resolved", "codex_turn_completed", "state_rehydrated", "stale_attention_timeout" };
    public static SafeEvent? Project(string line)
    {
        try { using var doc = JsonDocument.Parse(line); var r = doc.RootElement; var source = Text(r, "source"); var name = Text(r, "event"); if (!DateTimeOffset.TryParse(Text(r, "timestampUtc"), out var timestamp) || !AllowedShape(r, source, name) || !AllowedEvent(source, name)) return null;
            var previous=Text(r,"previous"); var current=Text(r,"current"); var state=Text(r,"state"); var reason=Text(r,"reason"); var status=Text(r,"status"); var decision=Text(r,"decision");
            if ((previous.Length>0&&!States.Contains(previous))||(current.Length>0&&!States.Contains(current))||(state.Length>0&&!States.Contains(state))||(reason.Length>0&&!Reasons.Contains(reason))) return null;
            if (source=="codex_stdio_bridge" && name=="turn_completed" && (Text(r,"schemaVersion")!="k15-codex-completion/v1" || !RequiredOpaque(r,"threadId") || !RequiredOpaque(r,"turnId"))) return null;
            if (source=="codex_stdio_bridge" && name=="approval_resolved" && (Text(r,"schemaVersion")!="k15-codex-approval/v1" || !RequiredOpaque(r,"rpcId") || Text(r,"rpcIdType") is not ("number" or "string"))) return null;
            if (source=="codex_stdio_bridge" && name=="turn_completed" && status is not ("completed" or "interrupted" or "failed")) return null;
            if (source=="codex_stdio_bridge" && name=="approval_resolved" && decision is not ("accept" or "acceptForSession" or "decline" or "cancel")) return null;
            return new SafeEvent(timestamp.ToUniversalTime(),source,name,Opaque(r,"sessionId"),Opaque(r,"turnId"),state,previous,current,reason,status,decision);
        } catch(JsonException){return null;}
    }
    private static bool AllowedEvent(string source,string name)=>source switch{"codex_hook"=>name is "UserPromptSubmit" or "PermissionRequest" or "PreToolUse" or "PostToolUse" or "Stop" or "SessionEnd","codex_stdio_bridge"=>name is "approval_resolved" or "turn_completed","state_normalizer"=>name is "normalized_state_changed" or "session_state_changed" or "state_rehydrated" or "normalizer_error","live_dashboard"=>name is "started" or "stopped" or "bind_failed" or "runtime_error","status_tray"=>name is "started" or "stopped",_=>false};
    private static bool AllowedShape(JsonElement r,string source,string name){var allowed=source switch{"codex_hook"=>new[]{"timestampUtc","source","event","sessionId","turnId","cwd","model","toolName","permissionMode"},"codex_stdio_bridge" when name=="turn_completed"=>new[]{"timestampUtc","source","event","schemaVersion","threadId","turnId","status"},"codex_stdio_bridge" when name=="approval_resolved"=>new[]{"timestampUtc","source","event","schemaVersion","decision","rpcIdType","rpcId","threadId","turnId","itemId"},"state_normalizer"=>new[]{"timestampUtc","source","event","plane","sessionId","previous","current","reason","sourceTimestampUtc","isRehydrated","correlation","focusedSessionId","focusedCwd","activeTaskSessions","replayWindowMinutes","aggregatePrevious","aggregateCurrent","driverSessionId","driverReason","runningCount","waitingCount","doneUnreadCount","attention","exception","hresult"},"live_dashboard"=>new[]{"timestampUtc","source","event","version","loopbackPort","exceptionType","hresult","category"},"status_tray"=>new[]{"timestampUtc","source","event"},_=>Array.Empty<string>()};return r.EnumerateObject().All(p=>allowed.Contains(p.Name,StringComparer.Ordinal))&&(!r.TryGetProperty("correlation",out var c)||ExactObject(c,"threadId","turnId","rpcIdType","rpcId"));}
    private static bool ExactObject(JsonElement x,params string[] keys)=>x.ValueKind==JsonValueKind.Object&&x.EnumerateObject().All(p=>keys.Contains(p.Name,StringComparer.Ordinal))&&x.EnumerateObject().All(p=>p.Value.ValueKind==JsonValueKind.String&&ByteLength(p.Value.GetString()??"")<=128);
    private static string Text(JsonElement r,string n)=>r.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString()??"":"";
    private static bool RequiredOpaque(JsonElement r,string n){var x=Text(r,n);return x.Length>0&&ByteLength(x)<=128;}
    private static string Opaque(JsonElement r,string n){var x=Text(r,n);return ByteLength(x)<=128?x:"";} private static int ByteLength(string x)=>System.Text.Encoding.UTF8.GetByteCount(x);
}

public sealed class JournalTailer
{
    private readonly ConcurrentQueue<SafeEvent> _events=new(); private readonly int _capacity; private readonly string _path; private long _offset; private string _remainder=""; private DateTime _lastWrite;
    public JournalTailer(string? path=null,int capacity=300){_path=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VOROTEX","K15 Status Lab","events.jsonl");_capacity=Math.Clamp(capacity,200,500);Load(_path+".1");}
    public IReadOnlyList<SafeEvent> Snapshot()=>_events.ToArray();
    public IReadOnlyList<SafeEvent> Poll(){var added=new List<SafeEvent>();try{if(!File.Exists(_path))return added;var i=new FileInfo(_path);if(i.Length<_offset||i.LastWriteTimeUtc<_lastWrite){_offset=0;_remainder="";}_lastWrite=i.LastWriteTimeUtc;using var s=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);s.Seek(_offset,SeekOrigin.Begin);using var reader=new StreamReader(s);var text=_remainder+reader.ReadToEnd();_offset=s.Position;var lines=text.Split('\n');for(var n=0;n<lines.Length-1;n++){var e=EventSanitizer.Project(lines[n].TrimEnd('\r'));if(e is not null){_events.Enqueue(e);added.Add(e);Trim();}}_remainder=text.EndsWith('\n')?"":lines[^1];}catch(IOException){}catch(UnauthorizedAccessException){}return added;}
    private void Load(string p){try{if(!File.Exists(p))return;foreach(var l in File.ReadLines(p)){var e=EventSanitizer.Project(l);if(e is not null)_events.Enqueue(e);Trim();}}catch(IOException){}catch(UnauthorizedAccessException){}} private void Trim(){while(_events.Count>_capacity)_events.TryDequeue(out _);}
}

public sealed class SnapshotPoller
{
    public StatusTraySnapshot? Snapshot{get;private set;} public bool Online{get;private set;}
    public async Task PollAsync(){try{var r=await StatusTrayIpc.SendAsync("snapshot",timeoutMs:700);Snapshot=r.Success?r.Snapshot:null;Online=r.Success&&r.Snapshot is not null;}catch{Snapshot=null;Online=false;}}
}

public sealed class SseClientBuffer
{
    private readonly Channel<SafeEvent> _channel;
    public SseClientBuffer(int capacity = 32) { _channel = Channel.CreateBounded<SafeEvent>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }); }
    public ChannelReader<SafeEvent> Reader => _channel.Reader;
    public bool TryWrite(SafeEvent item) => _channel.Writer.TryWrite(item);
}
