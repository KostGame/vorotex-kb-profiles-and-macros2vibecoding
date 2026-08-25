using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vorotex.K15.SleepSweepLab;

internal sealed record CaptureOutcome(bool Success, string Message, int? CurrentProfile, string ReportPath);

internal sealed class SweepState
{
    public int Schema { get; set; } = 1;
    public string Session { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SweepSnapshot> Snapshots { get; set; } = [];
}

internal sealed class SweepSnapshot
{
    public int Minute { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
    public int? CurrentProfile { get; set; }
    public List<SweepFile> Files { get; set; } = [];
}

internal sealed class SweepFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class SweepComparison
{
    public int FromMinute { get; set; }
    public int ToMinute { get; set; }
    public bool ProfileChanged { get; set; }
    public List<string> AddedFiles { get; set; } = [];
    public List<string> RemovedFiles { get; set; } = [];
    public List<SweepChangedFile> ChangedFiles { get; set; } = [];
}

internal sealed class SweepChangedFile
{
    public string Path { get; set; } = string.Empty;
    public string BeforeSha256 { get; set; } = string.Empty;
    public string AfterSha256 { get; set; } = string.Empty;
    public List<SweepLineChange> TextChanges { get; set; } = [];
}

internal sealed class SweepLineChange
{
    public int Line { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}

internal sealed class SleepSweepSession
{
    private const int MinMinute = 1;
    private const int MaxMinute = 10;
    private const long MaxTextDiffBytes = 1024 * 1024;
    private const int MaxTextDiffLines = 120;
    private const int MaxLineLength = 800;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VOROTEX", "K15 Sleep Sweep Lab");

    private static string ActiveSessionFile => Path.Combine(RootDirectory, "active-session.txt");

    public string SessionDirectory { get; private set; }
    public string ReportPath => Path.Combine(SessionDirectory, "sleep-sweep-report.json");
    public string TextReportPath => Path.Combine(SessionDirectory, "sleep-sweep-report.txt");
    public SweepState State { get; private set; }

    public IReadOnlyCollection<int> CapturedMinutes => State.Snapshots
        .Select(snapshot => snapshot.Minute)
        .Distinct()
        .OrderBy(value => value)
        .ToArray();

    public bool Complete => Enumerable.Range(MinMinute, MaxMinute)
        .All(minute => State.Snapshots.Any(snapshot => snapshot.Minute == minute));

    public SleepSweepSession()
    {
        Directory.CreateDirectory(RootDirectory);
        var activeName = File.Exists(ActiveSessionFile)
            ? File.ReadAllText(ActiveSessionFile, Encoding.UTF8).Trim()
            : string.Empty;
        var activeDirectory = string.IsNullOrWhiteSpace(activeName)
            ? string.Empty
            : Path.Combine(RootDirectory, activeName);

        if (!string.IsNullOrWhiteSpace(activeDirectory) && Directory.Exists(activeDirectory))
        {
            SessionDirectory = activeDirectory;
            State = LoadState(activeDirectory) ?? NewState(activeName);
        }
        else
        {
            (SessionDirectory, State) = CreateNewSession();
        }

        SaveStateAndReport();
    }

    public void Reset()
    {
        (SessionDirectory, State) = CreateNewSession();
        SaveStateAndReport();
    }

    public CaptureOutcome Capture(int minute)
    {
        if (minute is < MinMinute or > MaxMinute)
            return new(false, "Minute must be between 1 and 10.", null, ReportPath);

        var vendorRoot = FindVendorConfigRoot();
        if (vendorRoot is null)
            return new(false, "VOROTEX Config folder was not found. Install/open the official VOROTEX software first.", null, ReportPath);

        try
        {
            var snapshotRoot = Path.Combine(SessionDirectory, "snapshots", minute.ToString("D2"));
            if (Directory.Exists(snapshotRoot))
                Directory.Delete(snapshotRoot, recursive: true);
            Directory.CreateDirectory(snapshotRoot);

            var files = new List<SweepFile>();
            foreach (var source in Directory.EnumerateFiles(vendorRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(vendorRoot, source);
                var target = Path.Combine(snapshotRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);

                var info = new FileInfo(source);
                files.Add(new SweepFile
                {
                    Path = NormalizeRelative(relative),
                    Size = info.Length,
                    LastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Sha256 = Sha256(target)
                });
            }

            var profile = ReadCurrentProfile(Path.Combine(vendorRoot, "DeviceFeature.ini"));
            State.Snapshots.RemoveAll(snapshot => snapshot.Minute == minute);
            State.Snapshots.Add(new SweepSnapshot
            {
                Minute = minute,
                CapturedUtc = DateTimeOffset.UtcNow,
                CurrentProfile = profile,
                Files = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList()
            });
            State.Snapshots = State.Snapshots.OrderBy(snapshot => snapshot.Minute).ToList();
            SaveStateAndReport();

            var profileText = profile is null ? "unknown" : profile == 0 ? "A / slot 0" : profile == 1 ? "B / slot 1" : $"slot {profile}";
            return new(true,
                $"Captured {minute} min · profile {profileText} · files {files.Count}. {CapturedMinutes.Count}/10 ready.",
                profile,
                ReportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new(false, $"Capture failed: {ex.Message}", null, ReportPath);
        }
    }

    public static string? FindVendorExecutable()
    {
        foreach (var root in CandidateProgramRoots())
        {
            var path = Path.Combine(root, "VOROTEX-K15-PRO", "VOROTEX-K15-PRO.exe");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private (string Directory, SweepState State) CreateNewSession()
    {
        var name = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var directory = Path.Combine(RootDirectory, name);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "snapshots"));
        File.WriteAllText(ActiveSessionFile, name, new UTF8Encoding(false));
        return (directory, NewState(name));
    }

    private static SweepState NewState(string name) => new()
    {
        Schema = 1,
        Session = name,
        CreatedUtc = DateTimeOffset.UtcNow,
        Snapshots = []
    };

    private static SweepState? LoadState(string sessionDirectory)
    {
        var path = Path.Combine(sessionDirectory, "session-state.json");
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SweepState>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveStateAndReport()
    {
        Directory.CreateDirectory(SessionDirectory);
        File.WriteAllText(Path.Combine(SessionDirectory, "session-state.json"),
            JsonSerializer.Serialize(State, JsonOptions), new UTF8Encoding(false));

        var ordered = State.Snapshots.OrderBy(snapshot => snapshot.Minute).ToArray();
        var comparisons = BuildComparisons(ordered);
        var captured = ordered.Select(snapshot => snapshot.Minute).ToArray();
        var missing = Enumerable.Range(MinMinute, MaxMinute).Except(captured).ToArray();
        var profiles = ordered.Where(snapshot => snapshot.CurrentProfile.HasValue)
            .Select(snapshot => snapshot.CurrentProfile!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        var profileConsistent = ordered.Length == 0 || (profiles.Length <= 1 && ordered.All(snapshot => snapshot.CurrentProfile.HasValue));
        var changedEveryStep = ChangedInEveryAdjacentStep(comparisons);

        var report = new
        {
            schema = 1,
            session = State.Session,
            createdUtc = State.CreatedUtc,
            generatedUtc = DateTimeOffset.UtcNow,
            purpose = "VOROTEX K15 sleep timeout sweep 1..10 minutes",
            safety = new
            {
                vendorWritesPerformedBySleepSweepLab = false,
                hidWritesPerformed = false,
                rawVendorCopiesRemainLocal = true,
                sendOnlyThisReportUnlessRawFilesAreExplicitlyRequested = true
            },
            instructions = new
            {
                officialVorotexMustRemainOpen = true,
                keepSamePhysicalProfileForAllTenCaptures = true,
                setSleepMinuteThenPressMatchingCaptureButton = true
            },
            analysis = new
            {
                complete1To10 = missing.Length == 0,
                capturedMinutes = captured,
                missingMinutes = missing,
                profileConsistent,
                profileValues = profiles,
                contaminatedByProfileSwitch = profiles.Length > 1,
                filesChangedInEveryAdjacentCapturedStep = changedEveryStep,
                note = "If no stable text/file candidate tracks 1..10, sleep is likely sent directly to the device rather than persisted in Config."
            },
            snapshots = ordered,
            comparisons
        };

        File.WriteAllText(ReportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(TextReportPath, BuildTextReport(ordered, comparisons, missing, profiles, changedEveryStep), new UTF8Encoding(false));
    }

    private List<SweepComparison> BuildComparisons(IReadOnlyList<SweepSnapshot> ordered)
    {
        var result = new List<SweepComparison>();
        for (var index = 1; index < ordered.Count; index++)
        {
            var before = ordered[index - 1];
            var after = ordered[index];
            var beforeMap = before.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var afterMap = after.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var added = afterMap.Keys.Except(beforeMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            var removed = beforeMap.Keys.Except(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            var changed = new List<SweepChangedFile>();

            foreach (var path in beforeMap.Keys.Intersect(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
            {
                var left = beforeMap[path];
                var right = afterMap[path];
                if (string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                changed.Add(new SweepChangedFile
                {
                    Path = path,
                    BeforeSha256 = left.Sha256,
                    AfterSha256 = right.Sha256,
                    TextChanges = BuildTextChanges(before.Minute, after.Minute, path, left.Size, right.Size)
                });
            }

            result.Add(new SweepComparison
            {
                FromMinute = before.Minute,
                ToMinute = after.Minute,
                ProfileChanged = before.CurrentProfile != after.CurrentProfile,
                AddedFiles = added,
                RemovedFiles = removed,
                ChangedFiles = changed
            });
        }
        return result;
    }

    private List<SweepLineChange> BuildTextChanges(int beforeMinute, int afterMinute, string relativePath, long beforeSize, long afterSize)
    {
        if (!IsTextCandidate(relativePath) || beforeSize > MaxTextDiffBytes || afterSize > MaxTextDiffBytes)
            return [];

        var leftPath = Path.Combine(SessionDirectory, "snapshots", beforeMinute.ToString("D2"), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var rightPath = Path.Combine(SessionDirectory, "snapshots", afterMinute.ToString("D2"), relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var left = File.ReadAllLines(leftPath);
            var right = File.ReadAllLines(rightPath);
            var count = Math.Max(left.Length, right.Length);
            var result = new List<SweepLineChange>();
            for (var line = 0; line < count && result.Count < MaxTextDiffLines; line++)
            {
                var before = line < left.Length ? left[line] : null;
                var after = line < right.Length ? right[line] : null;
                if (string.Equals(before, after, StringComparison.Ordinal))
                    continue;
                result.Add(new SweepLineChange
                {
                    Line = line + 1,
                    Before = Truncate(before),
                    After = Truncate(after)
                });
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return [];
        }
    }

    private static string[] ChangedInEveryAdjacentStep(IReadOnlyList<SweepComparison> comparisons)
    {
        if (comparisons.Count == 0)
            return [];
        HashSet<string>? intersection = null;
        foreach (var comparison in comparisons)
        {
            var current = comparison.ChangedFiles.Select(file => file.Path)
                .Concat(comparison.AddedFiles)
                .Concat(comparison.RemovedFiles)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            intersection = intersection is null ? current : new HashSet<string>(intersection.Intersect(current, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }
        return intersection?.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }

    private static string BuildTextReport(
        IReadOnlyList<SweepSnapshot> snapshots,
        IReadOnlyList<SweepComparison> comparisons,
        IReadOnlyList<int> missing,
        IReadOnlyList<int> profiles,
        IReadOnlyList<string> changedEveryStep)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VOROTEX K15 Sleep Sweep Lab");
        sb.AppendLine("Sweep: 1..10 minutes");
        sb.AppendLine("Writes by lab: NONE");
        sb.AppendLine($"Captured: {string.Join(", ", snapshots.Select(snapshot => snapshot.Minute))}");
        sb.AppendLine($"Missing: {(missing.Count == 0 ? "NONE" : string.Join(", ", missing))}");
        sb.AppendLine($"Profiles observed: {(profiles.Count == 0 ? "UNKNOWN" : string.Join(", ", profiles))}");
        sb.AppendLine($"Contaminated by profile switch: {(profiles.Count > 1 ? "YES" : "NO")}");
        sb.AppendLine();
        foreach (var comparison in comparisons)
        {
            sb.AppendLine($"{comparison.FromMinute} -> {comparison.ToMinute}: changedFiles={comparison.ChangedFiles.Count}; profileChanged={comparison.ProfileChanged}");
            foreach (var file in comparison.ChangedFiles.Take(30))
            {
                sb.AppendLine($"  {file.Path}");
                foreach (var change in file.TextChanges.Take(20))
                    sb.AppendLine($"    line {change.Line}: {change.Before} -> {change.After}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Files changed in every adjacent captured step:");
        foreach (var path in changedEveryStep)
            sb.AppendLine($"  {path}");
        sb.AppendLine();
        sb.AppendLine("Send sleep-sweep-report.json to analysis. Raw snapshot copies remain local.");
        return sb.ToString();
    }

    private static int? ReadCurrentProfile(string deviceFeaturePath)
    {
        if (!File.Exists(deviceFeaturePath))
            return null;
        try
        {
            foreach (var line in File.ReadLines(deviceFeaturePath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("CurProfile=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                return int.TryParse(value, out var parsed) ? parsed : null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static string? FindVendorConfigRoot()
    {
        foreach (var root in CandidateProgramRoots())
        {
            var path = Path.Combine(root, "VOROTEX-K15-PRO", "res", "KeyboardDock", "KeyboardA", "Config");
            if (Directory.Exists(path))
                return path;
        }
        return null;
    }

    private static IEnumerable<string> CandidateProgramRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTextCandidate(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".toml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= MaxLineLength)
            return value;
        return value[..MaxLineLength] + "…";
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
