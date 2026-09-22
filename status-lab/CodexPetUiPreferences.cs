using System.Text.Json;

namespace Vorotex.K15.StatusLab;

// Stores presentation preference only. No task, session, title, or activity data.
internal sealed class CodexPetUiPreferenceStore
{
    internal const int MaximumBytes = 4096;
    internal const PetTaskSurfaceState SafeDefault = PetTaskSurfaceState.Stacked;
    internal static readonly CodexPetUiPreferences SafeDefaults = new(SafeDefault, CodexPetSizePolicy.DefaultPreset);

    internal string FilePath { get; }

    internal CodexPetUiPreferenceStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX", "K15 Status Lab", "codex-pet-ui.json");
    }

    internal CodexPetUiPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > MaximumBytes)
                return SafeDefaults;
            return Deserialize(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return SafeDefaults;
        }
    }

    internal void Save(CodexPetUiPreferences preferences)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("UI preference path has no directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(preferences));
        File.Move(temporaryPath, FilePath, true);
    }

    internal static string Serialize(CodexPetUiPreferences preferences) =>
        JsonSerializer.Serialize(new
        {
            presentationState = CodexPetTaskSurfacePolicy.Select(preferences.PresentationState).ToString(),
            petSize = CodexPetSizePolicy.Select(preferences.PetSize).ToString()
        });

    internal static CodexPetUiPreferences Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var state = ParseState(document.RootElement);
            var size = ParseSize(document.RootElement);
            return new(state, size);
        }
        catch (JsonException)
        {
            return SafeDefaults;
        }
    }

    private static PetTaskSurfaceState ParseState(JsonElement root)
    {
        if (!root.TryGetProperty("presentationState", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<PetTaskSurfaceState>(value.GetString(), false, out var state) ||
            !Enum.IsDefined(state))
            return SafeDefault;
        return state;
    }

    private static PetSizePreset ParseSize(JsonElement root)
    {
        if (!root.TryGetProperty("petSize", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<PetSizePreset>(value.GetString(), false, out var size) ||
            !Enum.IsDefined(size))
            return CodexPetSizePolicy.DefaultPreset;
        return size;
    }
}

internal readonly record struct CodexPetUiPreferences(
    PetTaskSurfaceState PresentationState,
    PetSizePreset PetSize);
