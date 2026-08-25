using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vorotex.K15.StatusLab;

internal sealed record DeviceResearchCaptureResult(bool Success, string Message, string? SessionDirectory = null, string? ReportPath = null);

internal static class DeviceSettingsResearch
{
    public static string RootDirectory { get; } = Path.Combine(EventJournal.DirectoryPath, "device-settings-research");
    private static string ActiveSessionFile => Path.Combine(RootDirectory, "active-session.txt");

    private sealed record Candidate(string Name, string RelativeVendorPath, bool Volatile);
    private sealed record FileMeta(string Name, bool Exists, long Size, DateTimeOffset? LastWriteUtc, string Sha256, bool Volatile);
    private sealed record LineChange(int Line, string? Before, string? After);
    private sealed record FileChange(string File, bool Changed, string Classification, string BeforeSha256, string AfterSha256,
        string[] JsonPaths, LineChange[] LineChanges);
    private sealed record SafetyInfo(bool VendorWritesPerformedByStatusLab, bool HidPowerWritesPerformed,
        bool RawCopiesRemainLocal, bool AutomaticPublication);
    private sealed record ResearchReport(int Schema, string Session, DateTimeOffset CreatedUtc, string Purpose,
        SafetyInfo Safety, List<FileChange> Files);

    private static readonly Candidate[] Candidates =
    [
        new("DeviceFeature.ini", @"KeyboardDock\KeyboardA\Config\DeviceFeature.ini", true),
        new("KBconfig.ini", @"KeyboardDock\KeyboardA\Config\KBconfig.ini", false),
        new("Profile0.json", @"KeyboardDock\KeyboardA\Config\Profile0.json", false),
        new("Profile1.json", @"KeyboardDock\KeyboardA\Config\Profile1.json", false)
    ];

    public static DeviceResearchCaptureResult CaptureBefore()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var sessionName = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var session = Path.Combine(RootDirectory, sessionName);
            Directory.CreateDirectory(Path.Combine(session, "before"));
            CaptureSide(session, "before");
            File.WriteAllText(ActiveSessionFile, sessionName, new UTF8Encoding(false));

            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "device_settings_research",
                @event = "capture_before",
                session = sessionName,
                writePolicy = "read_only_vendor_files"
            });

            return new(true,
                "BEFORE снят. Теперь в штатном VOROTEX измени только один параметр sleep/standby, сохрани его и нажми Capture AFTER.",
                session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, $"Не удалось снять BEFORE: {ex.Message}");
        }
    }

    public static DeviceResearchCaptureResult CaptureAfter()
    {
        try
        {
            if (!File.Exists(ActiveSessionFile))
                return new(false, "Нет активного BEFORE. Сначала нажми Capture BEFORE.");

            var sessionName = File.ReadAllText(ActiveSessionFile).Trim();
            if (string.IsNullOrWhiteSpace(sessionName))
                return new(false, "Активная research-сессия повреждена. Сними новый BEFORE.");

            var session = Path.Combine(RootDirectory, sessionName);
            if (!Directory.Exists(Path.Combine(session, "before")))
                return new(false, "Папка BEFORE не найдена. Сними новый BEFORE.");

            Directory.CreateDirectory(Path.Combine(session, "after"));
            CaptureSide(session, "after");
            var report = BuildReport(session, sessionName);
            var jsonPath = Path.Combine(session, "report.json");
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            var textPath = Path.Combine(session, "report.txt");
            File.WriteAllText(textPath, BuildTextReport(report), new UTF8Encoding(false));
            File.Delete(ActiveSessionFile);

            EventJournal.Append(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                source = "device_settings_research",
                @event = "capture_after_report_ready",
                session = sessionName,
                changedFiles = report.Files.Count(file => file.Changed),
                reportJson = Path.GetFileName(jsonPath)
            });

            return new(true,
                $"AFTER снят. Изменено файлов: {report.Files.Count(file => file.Changed)}. Локальный diff готов и не публикуется автоматически.",
                session, textPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, $"Не удалось снять AFTER/diff: {ex.Message}");
        }
    }

    public static string? FindVendorExecutable()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var path = Path.Combine(root, "VOROTEX-K15-PRO", "VOROTEX-K15-PRO.exe");
        return File.Exists(path) ? path : null;
    }

    private static void CaptureSide(string session, string side)
    {
        var targetRoot = Path.Combine(session, side);
        var vendorRoot = VendorResourceRoot();
        var manifest = new List<FileMeta>();

        foreach (var candidate in Candidates)
        {
            var source = Path.Combine(vendorRoot, candidate.RelativeVendorPath);
            if (!File.Exists(source))
            {
                manifest.Add(new(candidate.Name, false, 0, null, string.Empty, candidate.Volatile));
                continue;
            }

            var target = Path.Combine(targetRoot, candidate.Name);
            File.Copy(source, target, overwrite: true);
            var info = new FileInfo(source);
            manifest.Add(new(candidate.Name, true, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Sha256(target), candidate.Volatile));
        }

        File.WriteAllText(Path.Combine(targetRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static ResearchReport BuildReport(string session, string sessionName)
    {
        var beforeRoot = Path.Combine(session, "before");
        var afterRoot = Path.Combine(session, "after");
        var changes = new List<FileChange>();

        foreach (var candidate in Candidates)
        {
            var before = Path.Combine(beforeRoot, candidate.Name);
            var after = Path.Combine(afterRoot, candidate.Name);
            var beforeExists = File.Exists(before);
            var afterExists = File.Exists(after);
            var beforeHash = beforeExists ? Sha256(before) : string.Empty;
            var afterHash = afterExists ? Sha256(after) : string.Empty;
            var changed = beforeExists != afterExists || !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase);

            string[] jsonPaths = [];
            LineChange[] lineChanges = [];
            if (changed && beforeExists && afterExists)
            {
                if (candidate.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    jsonPaths = JsonDiffPaths(before, after).Take(250).ToArray();
                else
                    lineChanges = LineDiff(before, after).Take(250).ToArray();
            }

            changes.Add(new FileChange(candidate.Name, changed,
                candidate.Volatile ? "volatile_candidate" : "stable_candidate",
                beforeHash, afterHash, jsonPaths, lineChanges));
        }

        return new ResearchReport(
            1,
            sessionName,
            DateTimeOffset.UtcNow,
            "controlled sleep/standby diff",
            new SafetyInfo(false, false, true, false),
            changes);
    }

    private static string BuildTextReport(ResearchReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 Device Settings Research");
        sb.AppendLine($"Session: {report.Session}");
        sb.AppendLine("Status Lab vendor writes: NONE");
        sb.AppendLine("Unknown HID power writes: NONE");
        sb.AppendLine();
        foreach (var file in report.Files)
        {
            sb.AppendLine($"{file.File}: changed={file.Changed}; class={file.Classification}");
            foreach (var path in file.JsonPaths.Take(40))
                sb.AppendLine($"  JSON {path}");
            foreach (var line in file.LineChanges.Take(40))
                sb.AppendLine($"  line {line.Line}: {line.Before} -> {line.After}");
        }
        sb.AppendLine();
        sb.AppendLine("DeviceFeature.ini is intentionally classified as volatile_candidate.");
        sb.AppendLine("Raw before/after copies stay in this local research folder and are never published automatically.");
        return sb.ToString();
    }

    private static IEnumerable<string> JsonDiffPaths(string beforePath, string afterPath)
    {
        var before = JsonNode.Parse(File.ReadAllText(beforePath));
        var after = JsonNode.Parse(File.ReadAllText(afterPath));
        var result = new List<string>();
        Walk(before, after, "$", result);
        return result;
    }

    private static void Walk(JsonNode? before, JsonNode? after, string path, List<string> result)
    {
        if (JsonNode.DeepEquals(before, after))
            return;
        if (before is JsonObject bo && after is JsonObject ao)
        {
            foreach (var key in bo.Select(pair => pair.Key).Union(ao.Select(pair => pair.Key)).OrderBy(key => key, StringComparer.Ordinal))
            {
                bo.TryGetPropertyValue(key, out var beforeValue);
                ao.TryGetPropertyValue(key, out var afterValue);
                Walk(beforeValue, afterValue, path + "." + key, result);
            }
            return;
        }
        if (before is JsonArray ba && after is JsonArray aa)
        {
            var count = Math.Max(ba.Count, aa.Count);
            for (var i = 0; i < count; i++)
                Walk(i < ba.Count ? ba[i] : null, i < aa.Count ? aa[i] : null, $"{path}[{i}]", result);
            return;
        }
        result.Add(path);
    }

    private static IEnumerable<LineChange> LineDiff(string beforePath, string afterPath)
    {
        var before = File.ReadAllLines(beforePath);
        var after = File.ReadAllLines(afterPath);
        var count = Math.Max(before.Length, after.Length);
        for (var index = 0; index < count; index++)
        {
            var left = index < before.Length ? before[index] : null;
            var right = index < after.Length ? after[index] : null;
            if (!string.Equals(left, right, StringComparison.Ordinal))
                yield return new LineChange(index + 1, left, right);
        }
    }

    private static string VendorResourceRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "VOROTEX-K15-PRO", "res");

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
