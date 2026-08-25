using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vorotex.K15.HidResearchLab;

internal sealed record OwnerCaptureStatus(
    bool TargetProcessRunning,
    bool K15Present,
    int MarkerCount,
    int ChangedConfigFiles,
    string OutputDirectory);

internal sealed class KeyboardSleepCaptureSession : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);
    private static readonly string[] SafeIniKeys =
    [
        "SavePowerSelect", "SleepTime", "PDTime", "LODHeight",
        "StraightLineCorrectEn", "RippleCtrlEn", "LightControlEn"
    ];

    private readonly string _targetExe;
    private readonly string _installRoot;
    private readonly string _outputRoot;
    private readonly string _ownerActionsPath;
    private readonly string _configDeltaPath;
    private readonly string _devicePresencePath;
    private readonly string _runtimeObservationPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<string, FileSnapshot> _files = new(StringComparer.OrdinalIgnoreCase);

    private Task? _loopTask;
    private string _lastDeviceSignature = string.Empty;
    private string _lastForegroundSignature = string.Empty;
    private bool? _lastTargetProcessRunning;
    private int _markerCount;
    private int _changedConfigFiles;
    private bool _stopped;

    public KeyboardSleepCaptureSession(string targetExe, string outputRoot)
    {
        _targetExe = Path.GetFullPath(targetExe);
        _installRoot = Path.GetDirectoryName(_targetExe)!;
        _outputRoot = outputRoot;
        _ownerActionsPath = Path.Combine(outputRoot, "owner-actions.jsonl");
        _configDeltaPath = Path.Combine(outputRoot, "config-delta.jsonl");
        _devicePresencePath = Path.Combine(outputRoot, "device-presence.jsonl");
        _runtimeObservationPath = Path.Combine(outputRoot, "runtime-observation.jsonl");
    }

    public string OutputDirectory => _outputRoot;
    public event Action<OwnerCaptureStatus>? StatusChanged;

    public void Start()
    {
        if (_loopTask is not null)
            return;

        Directory.CreateDirectory(_outputRoot);
        var safety = SafetyHeader();
        AppendJson(_ownerActionsPath, new { timestampUtc = DateTimeOffset.UtcNow, @event = "safety_header", safety });
        AppendJson(_configDeltaPath, new { timestampUtc = DateTimeOffset.UtcNow, @event = "safety_header", safety });
        AppendJson(_devicePresencePath, new { timestampUtc = DateTimeOffset.UtcNow, @event = "safety_header", safety });
        AppendJson(_runtimeObservationPath, new { timestampUtc = DateTimeOffset.UtcNow, @event = "safety_header", safety });
        AppendJson(_ownerActionsPath, new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            @event = "capture_started",
            targetExecutable = Path.GetFileName(_targetExe),
            targetExecutableSha256 = HashFile(_targetExe),
            pollIntervalMs = (int)PollInterval.TotalMilliseconds
        });

        CaptureConfigBaseline();
        ObserveDevices(force: true);
        ObserveRuntime(force: true);
        PublishStatus();
        _loopTask = Task.Run(ProcessLoopAsync);
    }

    public void MarkAction(string label)
    {
        if (_stopped)
            throw new InvalidOperationException("Capture is already stopped.");
        label = (label ?? string.Empty).Trim();
        if (label.Length == 0)
            throw new InvalidOperationException("Enter a short action marker first.");
        if (label.Length > 160)
            label = label[..160];

        var foreground = ForegroundWindowProbe.Snapshot();
        Interlocked.Increment(ref _markerCount);
        AppendJson(_ownerActionsPath, new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            @event = "owner_marker",
            label,
            foreground = foreground is null ? null : new
            {
                foreground.ProcessId,
                foreground.ProcessName,
                foreground.TitleLength,
                foreground.TitleSha256
            }
        });
        PublishStatus();
    }

    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }
        AppendJson(_ownerActionsPath, new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            @event = "capture_stopped",
            markerCount = _markerCount,
            changedConfigFiles = _changedConfigFiles
        });
        PublishStatus();
    }

    private async Task ProcessLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                ObserveConfigChanges();
                ObserveDevices(force: false);
                ObserveRuntime(force: false);
                PublishStatus();
            }
            catch (Exception ex)
            {
                AppendJson(_runtimeObservationPath, new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    @event = "observer_error",
                    exception = ex.GetType().FullName,
                    message = ex.Message
                });
            }

            try { await Task.Delay(PollInterval, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void CaptureConfigBaseline()
    {
        foreach (var path in EnumerateRelevantConfigFiles())
        {
            var snapshot = SnapshotFile(path);
            if (snapshot is null)
                continue;
            _files[path] = snapshot;
            AppendJson(_configDeltaPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                @event = "config_baseline",
                relativePath = Path.GetRelativePath(_installRoot, path),
                snapshot.Length,
                snapshot.LastWriteUtc,
                snapshot.Sha256,
                safeKeys = snapshot.SafeKeys
            });
        }
    }

    private void ObserveConfigChanges()
    {
        var currentPaths = EnumerateRelevantConfigFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in currentPaths)
        {
            var current = SnapshotFile(path);
            if (current is null)
                continue;

            if (!_files.TryGetValue(path, out var previous))
            {
                _files[path] = current;
                Interlocked.Increment(ref _changedConfigFiles);
                AppendJson(_configDeltaPath, new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    @event = "config_created",
                    relativePath = Path.GetRelativePath(_installRoot, path),
                    current.Length,
                    current.LastWriteUtc,
                    current.Sha256,
                    safeKeys = current.SafeKeys
                });
                continue;
            }

            if (previous.Sha256 == current.Sha256 && previous.Length == current.Length)
                continue;

            _files[path] = current;
            Interlocked.Increment(ref _changedConfigFiles);
            AppendJson(_configDeltaPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                @event = "config_changed",
                relativePath = Path.GetRelativePath(_installRoot, path),
                before = new { previous.Length, previous.LastWriteUtc, previous.Sha256, safeKeys = previous.SafeKeys },
                after = new { current.Length, current.LastWriteUtc, current.Sha256, safeKeys = current.SafeKeys }
            });
        }

        foreach (var removed in _files.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
        {
            var previous = _files[removed];
            _files.Remove(removed);
            Interlocked.Increment(ref _changedConfigFiles);
            AppendJson(_configDeltaPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                @event = "config_removed",
                relativePath = Path.GetRelativePath(_installRoot, removed),
                previous.Length,
                previous.LastWriteUtc,
                previous.Sha256,
                safeKeys = previous.SafeKeys
            });
        }
    }

    private void ObserveDevices(bool force)
    {
        var devices = HidPresenceProbe.EnumerateKnownK15();
        var signature = string.Join("|", devices.Select(d => $"{d.Vid:X4}:{d.Pid:X4}:{d.InterfacePathSha256}"));
        if (!force && signature == _lastDeviceSignature)
            return;
        _lastDeviceSignature = signature;
        AppendJson(_devicePresencePath, new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            @event = "k15_presence_changed",
            present = devices.Count > 0,
            deviceCount = devices.Count,
            devices
        });
    }

    private void ObserveRuntime(bool force)
    {
        var running = IsTargetProcessRunning();
        if (force || _lastTargetProcessRunning != running)
        {
            _lastTargetProcessRunning = running;
            AppendJson(_runtimeObservationPath, new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                @event = "target_process_state",
                running,
                executable = Path.GetFileName(_targetExe)
            });
        }

        var foreground = ForegroundWindowProbe.Snapshot();
        var signature = foreground is null
            ? "none"
            : $"{foreground.ProcessId}:{foreground.ProcessName}:{foreground.TitleLength}:{foreground.TitleSha256}";
        if (!force && signature == _lastForegroundSignature)
            return;
        _lastForegroundSignature = signature;
        AppendJson(_runtimeObservationPath, new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            @event = "foreground_changed",
            foreground = foreground is null ? null : new
            {
                foreground.ProcessId,
                foreground.ProcessName,
                foreground.TitleLength,
                foreground.TitleSha256
            }
        });
    }

    private bool IsTargetProcessRunning()
    {
        var processName = Path.GetFileNameWithoutExtension(_targetExe);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) ||
                        string.Equals(Path.GetFullPath(path), _targetExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    return true;
                }
            }
        }
        return false;
    }

    private IEnumerable<string> EnumerateRelevantConfigFiles()
    {
        var setCandidates = new[]
        {
            Path.Combine(_installRoot, "res", "Set.ini"),
            Path.Combine(_installRoot, "res_black", "Set.ini")
        };
        foreach (var path in setCandidates)
        {
            if (File.Exists(path))
                yield return path;
        }

        foreach (var root in new[]
        {
            Path.Combine(_installRoot, "res", "KeyboardDock"),
            Path.Combine(_installRoot, "res_black", "KeyboardDock")
        })
        {
            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files
                         .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Config{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                         .Take(300))
            {
                yield return file;
            }
        }
    }

    private static FileSnapshot? SnapshotFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 32L * 1024 * 1024)
                return null;
            return new FileSnapshot(
                info.Length,
                info.LastWriteTimeUtc,
                HashFile(path),
                Path.GetFileName(path).Equals("Set.ini", StringComparison.OrdinalIgnoreCase)
                    ? ReadSafeIniKeys(path)
                    : new Dictionary<string, string>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadSafeIniKeys(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(path).Take(1000))
            {
                var trimmed = line.Trim();
                foreach (var key in SafeIniKeys)
                {
                    if (!trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var value = trimmed[(key.Length + 1)..].Trim();
                    if (value.Length > 80)
                        value = value[..80];
                    result[key] = value;
                }
            }
        }
        catch
        {
        }
        return result;
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void AppendJson(string path, object value)
    {
        var line = JsonSerializer.Serialize(value) + Environment.NewLine;
        lock (_writeGate)
            File.AppendAllText(path, line, new UTF8Encoding(false));
    }

    private static object SafetyHeader() => new
    {
        mode = "read_only_owner_interaction_capture",
        executableModified = false,
        executablePatched = false,
        processAttached = false,
        processInjected = false,
        debuggerAttached = false,
        deviceOpened = false,
        featureReportsQueried = false,
        hidWritesPerformed = false,
        unknownSelectorsProbed = false,
        profileSelectionChanged = false
    };

    private void PublishStatus()
    {
        var devices = HidPresenceProbe.EnumerateKnownK15();
        StatusChanged?.Invoke(new OwnerCaptureStatus(
            _lastTargetProcessRunning == true,
            devices.Count > 0,
            _markerCount,
            _changedConfigFiles,
            _outputRoot));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private sealed record FileSnapshot(
        long Length,
        DateTime LastWriteUtc,
        string Sha256,
        Dictionary<string, string> SafeKeys);
}

internal static class ForegroundWindowProbe
{
    internal sealed record ForegroundSnapshot(int ProcessId, string ProcessName, int TitleLength, string TitleSha256);

    public static ForegroundSnapshot? Snapshot()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        var length = Math.Clamp(GetWindowTextLength(hwnd), 0, 4096);
        var buffer = new StringBuilder(length + 1);
        if (length > 0)
            GetWindowText(hwnd, buffer, buffer.Capacity);
        var title = buffer.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title))).ToLowerInvariant();
        var processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch
        {
        }
        return new ForegroundSnapshot((int)pid, processName, title.Length, hash);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}

internal sealed record HidPresenceDevice(ushort Vid, ushort Pid, string InterfacePathSha256);

internal static class HidPresenceProbe
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private static readonly Regex VidPid = new(@"vid_([0-9a-f]{4}).*pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<HidPresenceDevice> EnumerateKnownK15()
    {
        var result = new List<HidPresenceDevice>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259)
                        break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0)
                    continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                        continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    var match = VidPid.Match(path);
                    if (!match.Success ||
                        !ushort.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var vid) ||
                        !ushort.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var pid))
                        continue;
                    if ((vid is not (0x36A4 or 0xB6A4)) || (pid is not (0x4100 or 0x4101)))
                        continue;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
                    result.Add(new HidPresenceDevice(vid, pid, hash));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result
            .GroupBy(d => d.InterfacePathSha256, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(d => d.Vid)
            .ThenBy(d => d.Pid)
            .ThenBy(d => d.InterfacePathSha256, StringComparer.Ordinal)
            .ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
