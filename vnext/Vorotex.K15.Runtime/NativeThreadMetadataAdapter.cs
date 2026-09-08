using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.Runtime;

public sealed record NativeThreadMetadataEvent(string ThreadId, string WorkingDirectory);
public sealed record NativeThreadMetadataParseResult(NativeThreadMetadataEvent? Event, ImmutableArray<string> Diagnostics)
{ public bool IsValid => Event is not null && Diagnostics.IsEmpty; }

public static class NativeThreadMetadataAdapter
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "schemaVersion", "source", "event", "threadId", "workingDirectory" };

    public static NativeThreadMetadataParseResult Parse(string json)
    {
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { diagnostics.Add("NATIVE_METADATA_NOT_OBJECT"); return Invalid(diagnostics); }
            foreach (var property in root.EnumerateObject()) if (!Allowed.Contains(property.Name)) diagnostics.Add("UNSUPPORTED_NATIVE_METADATA_FIELD");
            if (!String(root, "schemaVersion", "k15-codex-thread-metadata/v1", out _)) diagnostics.Add("INVALID_NATIVE_METADATA_SCHEMA");
            if (!String(root, "source", "codex_stdio_bridge", out _)) diagnostics.Add("INVALID_NATIVE_METADATA_SOURCE");
            if (!String(root, "event", "thread_metadata_changed", out _)) diagnostics.Add("INVALID_NATIVE_METADATA_EVENT");
            if (!String(root, "threadId", null, out var threadId)) diagnostics.Add("MISSING_NATIVE_THREAD_ID");
            if (!String(root, "workingDirectory", null, out var cwd) || string.IsNullOrWhiteSpace(cwd)) diagnostics.Add("MISSING_NATIVE_WORKING_DIRECTORY");
            else if (Encoding.UTF8.GetByteCount(cwd!) > 1024) diagnostics.Add("NATIVE_WORKING_DIRECTORY_TOO_LONG");
            if (diagnostics.Count != 0) return Invalid(diagnostics);
            return new(new NativeThreadMetadataEvent(threadId!, cwd!), ImmutableArray<string>.Empty);
        }
        catch (JsonException) { diagnostics.Add("MALFORMED_NATIVE_METADATA_PAYLOAD"); return Invalid(diagnostics); }
    }
    private static bool String(JsonElement root, string name, string? expected, out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        { value = element.GetString(); return expected is null || value == expected; }
        value = null; return false;
    }
    private static NativeThreadMetadataParseResult Invalid(ImmutableArray<string>.Builder diagnostics) => new(null, diagnostics.ToImmutable());
}
