using System.Text.Json;

namespace Vorotex.K15.StatusLab;

// Stores presentation preference only. No task, session, title, or activity data.
internal sealed class CodexPetUiPreferenceStore
{
    internal const int MaximumBytes = 4096;
    internal const PetTaskSurfaceState SafeDefault = PetTaskSurfaceState.Stacked;

    internal string FilePath { get; }

    internal CodexPetUiPreferenceStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX", "K15 Status Lab", "codex-pet-ui.json");
    }

    internal PetTaskSurfaceState Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > MaximumBytes)
                return SafeDefault;
            return Deserialize(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return SafeDefault;
        }
    }

    internal void Save(PetTaskSurfaceState state)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("UI preference path has no directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(state));
        File.Move(temporaryPath, FilePath, true);
    }

    internal static string Serialize(PetTaskSurfaceState state) =>
        JsonSerializer.Serialize(new
        {
            presentationState = CodexPetTaskSurfacePolicy.Select(state).ToString()
        });

    internal static PetTaskSurfaceState Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("presentationState", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<PetTaskSurfaceState>(value.GetString(), false, out var state) ||
                !Enum.IsDefined(state))
                return SafeDefault;
            return state;
        }
        catch (JsonException)
        {
            return SafeDefault;
        }
    }
}
