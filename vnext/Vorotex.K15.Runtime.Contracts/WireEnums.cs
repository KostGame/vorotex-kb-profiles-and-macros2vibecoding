using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

/// <summary>
/// Normalized state owned by the vNext runtime process.
/// </summary>
[JsonConverter(typeof(RuntimeStateJsonConverter))]
public enum RuntimeState
{
    Unknown = 0,
    Normal = 1,
    Running = 2,
    Waiting = 3,
    DonePendingAttention = 4,
    Blocked = 5,
}

/// <summary>
/// Runtime-level status for a thread known to the future state adapter.
/// </summary>
[JsonConverter(typeof(ThreadRuntimeStatusJsonConverter))]
public enum ThreadRuntimeStatus
{
    Unknown = 0,
    NotLoaded = 1,
    Idle = 2,
    Active = 3,
    SystemError = 4,
}

/// <summary>
/// The active reason that requires attention from a future UI client.
/// </summary>
[JsonConverter(typeof(ThreadActiveFlagJsonConverter))]
public enum ThreadActiveFlag
{
    Unknown = 0,
    WaitingOnApproval = 1,
    WaitingOnUserInput = 2,
}

public sealed class RuntimeStateJsonConverter : JsonConverter<RuntimeState>
{
    public override bool HandleNull => true;

    public override RuntimeState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return RuntimeStateWire.Parse(reader.GetString());
        }

        reader.Skip();
        return RuntimeState.Unknown;
    }

    public override void Write(
        Utf8JsonWriter writer,
        RuntimeState value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(RuntimeStateWire.Format(value));
    }
}

public sealed class ThreadRuntimeStatusJsonConverter : JsonConverter<ThreadRuntimeStatus>
{
    public override bool HandleNull => true;

    public override ThreadRuntimeStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return ThreadRuntimeStatusWire.Parse(reader.GetString());
        }

        reader.Skip();
        return ThreadRuntimeStatus.Unknown;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ThreadRuntimeStatus value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(ThreadRuntimeStatusWire.Format(value));
    }
}

public sealed class ThreadActiveFlagJsonConverter : JsonConverter<ThreadActiveFlag>
{
    public override bool HandleNull => true;

    public override ThreadActiveFlag Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return ThreadActiveFlagWire.Parse(reader.GetString());
        }

        reader.Skip();
        return ThreadActiveFlag.Unknown;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ThreadActiveFlag value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(ThreadActiveFlagWire.Format(value));
    }
}

internal static class RuntimeStateWire
{
    public static string Format(RuntimeState value) => value switch
    {
        RuntimeState.Normal => "NORMAL",
        RuntimeState.Running => "RUNNING",
        RuntimeState.Waiting => "WAITING",
        RuntimeState.DonePendingAttention => "DONE_PENDING_ATTENTION",
        RuntimeState.Blocked => "BLOCKED",
        _ => "UNKNOWN",
    };

    public static RuntimeState Parse(string? value) => value switch
    {
        "NORMAL" => RuntimeState.Normal,
        "RUNNING" => RuntimeState.Running,
        "WAITING" => RuntimeState.Waiting,
        "DONE_PENDING_ATTENTION" => RuntimeState.DonePendingAttention,
        "BLOCKED" => RuntimeState.Blocked,
        _ => RuntimeState.Unknown,
    };
}

internal static class ThreadRuntimeStatusWire
{
    public static string Format(ThreadRuntimeStatus value) => value switch
    {
        ThreadRuntimeStatus.NotLoaded => "NOT_LOADED",
        ThreadRuntimeStatus.Idle => "IDLE",
        ThreadRuntimeStatus.Active => "ACTIVE",
        ThreadRuntimeStatus.SystemError => "SYSTEM_ERROR",
        _ => "UNKNOWN",
    };

    public static ThreadRuntimeStatus Parse(string? value) => value switch
    {
        "NOT_LOADED" => ThreadRuntimeStatus.NotLoaded,
        "IDLE" => ThreadRuntimeStatus.Idle,
        "ACTIVE" => ThreadRuntimeStatus.Active,
        "SYSTEM_ERROR" => ThreadRuntimeStatus.SystemError,
        _ => ThreadRuntimeStatus.Unknown,
    };
}

internal static class ThreadActiveFlagWire
{
    public static string Format(ThreadActiveFlag value) => value switch
    {
        ThreadActiveFlag.WaitingOnApproval => "WAITING_ON_APPROVAL",
        ThreadActiveFlag.WaitingOnUserInput => "WAITING_ON_USER_INPUT",
        _ => "UNKNOWN",
    };

    public static ThreadActiveFlag Parse(string? value) => value switch
    {
        "WAITING_ON_APPROVAL" => ThreadActiveFlag.WaitingOnApproval,
        "WAITING_ON_USER_INPUT" => ThreadActiveFlag.WaitingOnUserInput,
        _ => ThreadActiveFlag.Unknown,
    };
}
