using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vorotex.K15.Runtime.Contracts;

public static class RuntimeContractJson
{
    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, CreateOptions());

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, CreateOptions());
}
