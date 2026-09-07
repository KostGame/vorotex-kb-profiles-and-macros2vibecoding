using System.Collections.Immutable;
using System.Text.Json;
using Vorotex.K15.Runtime.Contracts;

namespace Vorotex.K15.Runtime;

/// <summary>
/// The only native data accepted by the vNext adapter. This is semantic
/// metadata, not an arbitrary App Server or JSON-RPC envelope.
/// </summary>
public sealed record NativeThreadStatusEvent(
    string SchemaVersion,
    string Source,
    string Event,
    string ThreadId,
    ThreadRuntimeStatus Status,
    ImmutableArray<ThreadActiveFlag> ActiveFlags,
    DateTimeOffset ObservedUtc,
    ThreadClassification Classification = ThreadClassification.User)
{
    public bool IsServiceOrCanary => Classification != ThreadClassification.User;
}

public sealed record NativeThreadStatusParseResult(
    NativeThreadStatusEvent? Event,
    ImmutableArray<string> Diagnostics)
{
    public bool IsValid => Event is not null && Diagnostics.IsEmpty;
}

/// <summary>
/// Parses sanitized native thread/status/changed metadata and delegates all
/// runtime-state semantics to <see cref="RuntimeStateEngine"/>.
/// </summary>
public static class NativeThreadStatusAdapter
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "source", "event", "threadId", "status", "activeFlags", "timestampUtc", "classification",
    };

    public static NativeThreadStatusParseResult Parse(string json)
    {
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid(diagnostics, "MALFORMED_NATIVE_STATUS_PAYLOAD");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(diagnostics, "NATIVE_STATUS_PAYLOAD_MUST_BE_OBJECT");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name))
                {
                    diagnostics.Add("UNSUPPORTED_NATIVE_STATUS_FIELD");
                }
            }

            if (!TryGetString(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != "k15-codex-thread-status/v1") diagnostics.Add("INVALID_NATIVE_STATUS_SCHEMA");
            if (!TryGetString(root, "source", out var source) || source != "codex_stdio_bridge")
                diagnostics.Add("INVALID_NATIVE_STATUS_SOURCE");
            if (!TryGetString(root, "event", out var eventName) || eventName != "thread_status_changed")
                diagnostics.Add("INVALID_NATIVE_STATUS_EVENT");

            if (!TryGetString(root, "threadId", out var threadId) || string.IsNullOrWhiteSpace(threadId))
            {
                diagnostics.Add("MISSING_NATIVE_THREAD_ID");
            }

            if (!TryGetString(root, "status", out var statusText))
            {
                diagnostics.Add("MISSING_NATIVE_STATUS");
            }

            var parsedTimestamp = default(DateTimeOffset);
            var timestampCandidate = default(DateTimeOffset);
            var hasTimestamp = TryGetString(root, "timestampUtc", out var timestampText)
                && DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out timestampCandidate);
            if (hasTimestamp)
            {
                parsedTimestamp = timestampCandidate;
            }
            else
            {
                diagnostics.Add("MISSING_OR_INVALID_NATIVE_TIMESTAMP");
            }

            var status = ParseStatus(statusText);
            if (status == ThreadRuntimeStatus.Unknown)
            {
                diagnostics.Add("UNKNOWN_NATIVE_STATUS");
            }

            var flags = ImmutableArray.CreateBuilder<ThreadActiveFlag>();
            if (!root.TryGetProperty("activeFlags", out var flagsElement)
                || flagsElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add("MISSING_OR_INVALID_NATIVE_ACTIVE_FLAGS");
            }
            else
            {
                foreach (var flagElement in flagsElement.EnumerateArray())
                {
                    if (flagElement.ValueKind != JsonValueKind.String)
                    {
                        diagnostics.Add("INVALID_NATIVE_ACTIVE_FLAG");
                        continue;
                    }

                    var flagText = flagElement.GetString();
                    var flag = ParseFlag(flagText);
                    if (flag == ThreadActiveFlag.Unknown)
                    {
                        diagnostics.Add("UNKNOWN_NATIVE_ACTIVE_FLAG");
                    }
                    flags.Add(flag);
                }
            }

            var classification = ThreadClassification.User;
            if (root.TryGetProperty("classification", out var classificationElement))
            {
                if (classificationElement.ValueKind != JsonValueKind.String
                    || !TryParseClassification(classificationElement.GetString(), out classification))
                {
                    diagnostics.Add("UNKNOWN_NATIVE_THREAD_CLASSIFICATION");
                }
            }

            if (diagnostics.Count != 0)
            {
                return Invalid(diagnostics);
            }

            return new NativeThreadStatusParseResult(
                new NativeThreadStatusEvent(schemaVersion!, source!, eventName!, threadId!, status,
                    CanonicalizeFlags(flags), parsedTimestamp, classification),
                ImmutableArray<string>.Empty);
        }
        catch (JsonException)
        {
            return Invalid(diagnostics, "MALFORMED_NATIVE_STATUS_PAYLOAD");
        }
    }

    public static ThreadRuntimeObservation ToObservation(NativeThreadStatusEvent nativeEvent)
    {
        ArgumentNullException.ThrowIfNull(nativeEvent);
        return new ThreadRuntimeObservation(
            nativeEvent.ThreadId,
            nativeEvent.Status,
            nativeEvent.ActiveFlags,
            nativeEvent.ObservedUtc,
            classification: nativeEvent.Classification);
    }

    private static NativeThreadStatusParseResult Invalid(
        ImmutableArray<string>.Builder diagnostics, string? diagnostic = null)
    {
        if (diagnostic is not null) diagnostics.Add(diagnostic);
        return new NativeThreadStatusParseResult(null, diagnostics.ToImmutable());
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }
        value = null;
        return false;
    }

    private static ImmutableArray<ThreadActiveFlag> CanonicalizeFlags(
        ImmutableArray<ThreadActiveFlag>.Builder flags) => flags
        .Distinct()
        .OrderBy(flag => flag switch
        {
            ThreadActiveFlag.WaitingOnApproval => 0,
            ThreadActiveFlag.WaitingOnUserInput => 1,
            _ => 2,
        })
        .ThenBy(flag => (int)flag)
        .ToImmutableArray();

    private static ThreadRuntimeStatus ParseStatus(string? value) => value switch
    {
        "notLoaded" => ThreadRuntimeStatus.NotLoaded,
        "idle" => ThreadRuntimeStatus.Idle,
        "active" => ThreadRuntimeStatus.Active,
        "systemError" => ThreadRuntimeStatus.SystemError,
        _ => ThreadRuntimeStatus.Unknown,
    };

    private static ThreadActiveFlag ParseFlag(string? value) => value switch
    {
        "waitingOnApproval" => ThreadActiveFlag.WaitingOnApproval,
        "waitingOnUserInput" => ThreadActiveFlag.WaitingOnUserInput,
        _ => ThreadActiveFlag.Unknown,
    };

    private static bool TryParseClassification(string? value, out ThreadClassification classification)
    {
        classification = value switch
        {
            "user" => ThreadClassification.User,
            "service" => ThreadClassification.Service,
            "canary" => ThreadClassification.Canary,
            _ => (ThreadClassification)(-1),
        };
        return classification != (ThreadClassification)(-1);
    }
}
