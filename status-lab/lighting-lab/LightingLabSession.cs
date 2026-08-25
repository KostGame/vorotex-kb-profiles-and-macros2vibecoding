using System.Text;
using System.Text.Json;
using Vorotex.K15.StatusLab;

namespace Vorotex.K15.LightingLab;

internal sealed record LightingLabTestRequest(
    K15LightingMode Mode,
    int Brightness,
    int Speed,
    int Direction,
    string[] Colors,
    byte PaletteMask,
    WireColorOrder WireColorOrder,
    string UserNote);

internal sealed record LightingLabTestResult(
    string TestId,
    byte OnboardSlot,
    string Profile,
    string Mode,
    byte ModeCode,
    string LogPath);

internal sealed class LightingLabSession : IDisposable
{
    private readonly object _logGate = new();
    private readonly K15HidLightingController _controller;
    private readonly Dictionary<byte, K15HidLightingController.LightingSnapshot> _snapshots = new();
    private bool _disposed;

    public string LogPath { get; }

    public LightingLabSession()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VOROTEX", "K15 Lighting Lab");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, "lighting-lab.jsonl");
        _controller = K15HidLightingController.Open();
        Append("lab_started", new { policy = "observe_physical_profile_only", logPath = LogPath });
    }

    public byte ReadActiveSlot() => _controller.ReadActiveSlot();

    public K15HidLightingController.LightingSnapshot EnsureSnapshot()
    {
        var slot = _controller.ReadActiveSlot();
        if (_snapshots.TryGetValue(slot, out var known))
            return known;

        var captureConfig = CreateResearchCaptureConfig();
        var snapshot = _controller.PrepareProfileSnapshot(captureConfig);
        if (snapshot.OnboardSlot != slot)
            throw new TimeoutException("K15 profile changed while Lighting Lab was capturing an exact baseline.");

        _snapshots[slot] = snapshot;
        Append("baseline_captured", new
        {
            onboardSlot = slot,
            profile = ProfileName(slot),
            header = Hex(snapshot.Header),
            modeRecords = snapshot.ModeRecords.ToDictionary(
                pair => $"0x{pair.Key:X2}", pair => Hex(pair.Value))
        });
        return snapshot;
    }

    public LightingLabTestResult Apply(LightingLabTestRequest request)
    {
        ThrowIfDisposed();
        var snapshot = EnsureSnapshot();
        var currentSlot = _controller.ReadActiveSlot();
        if (currentSlot != snapshot.OnboardSlot)
            throw new K15HidLightingController.K15ProfileChangedException(snapshot.OnboardSlot, currentSlot);

        // Every test starts from the captured exact baseline for deterministic comparison.
        _controller.Restore(snapshot);

        var effect = new LightingEffectConfig
        {
            Enabled = true,
            Mode = request.Mode,
            Brightness = request.Brightness,
            Speed = request.Speed,
            Direction = request.Direction,
            DurationSeconds = 0,
            Colors = request.Colors.ToArray(),
            PaletteMask = request.PaletteMask
        };

        var header = K15HidProtocol.CreateEffectHeader(snapshot.Header, effect);
        byte[]? detail = request.Mode == K15LightingMode.Off
            ? null
            : K15HidProtocol.CreateEffectRecord(effect, request.WireColorOrder);

        var testId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss.fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        _controller.ApplyEffect(snapshot, effect, request.WireColorOrder, $"LightingLab {testId}");

        Append("test_applied", new
        {
            testId,
            timestampUtc = DateTimeOffset.UtcNow,
            onboardSlot = snapshot.OnboardSlot,
            profile = ProfileName(snapshot.OnboardSlot),
            mode = UiModeName(request.Mode),
            nativeModeCode = $"0x{K15HidProtocol.ModeCode(request.Mode):X2}",
            brightness = request.Brightness,
            speed = request.Speed,
            direction = request.Direction,
            paletteMask = $"0x{request.PaletteMask:X2}",
            colors = request.Colors,
            wireColorOrder = request.WireColorOrder.ToString(),
            baselineHeader = Hex(snapshot.Header),
            writtenHeader = Hex(header),
            writtenModeRecord = detail is null ? null : Hex(detail),
            readback = "PASS",
            userNote = request.UserNote,
            colorModel = ColorModel(request.Mode)
        });

        return new LightingLabTestResult(
            testId,
            snapshot.OnboardSlot,
            ProfileName(snapshot.OnboardSlot),
            UiModeName(request.Mode),
            K15HidProtocol.ModeCode(request.Mode),
            LogPath);
    }

    public void RestoreCurrent(string reason = "manual_restore")
    {
        ThrowIfDisposed();
        var slot = _controller.ReadActiveSlot();
        if (!_snapshots.TryGetValue(slot, out var snapshot))
        {
            Append("restore_skipped_no_snapshot", new { reason, onboardSlot = slot });
            return;
        }

        _controller.Restore(snapshot);
        Append("baseline_restored", new
        {
            reason,
            onboardSlot = slot,
            profile = ProfileName(slot),
            header = Hex(snapshot.Header)
        });
    }

    public void AddUserNote(string testId, string note)
    {
        ThrowIfDisposed();
        Append("user_note", new
        {
            testId,
            timestampUtc = DateTimeOffset.UtcNow,
            note = note.Trim()
        });
    }

    private static StatusLabConfig CreateResearchCaptureConfig()
    {
        var config = StatusLabConfig.CreateDefault();
        config.States.Running.Mode = K15LightingMode.CycleBreathing;
        config.States.Waiting.Mode = K15LightingMode.TetrisBlocks;
        config.States.Done.Mode = K15LightingMode.Neon;
        config.States.Error.Mode = K15LightingMode.Ambilight;
        config.ProfileSwitch.Mode = K15LightingMode.Constant;
        config.ActivationSignal.Mode = K15LightingMode.FlowingWater;
        // PrepareProfileSnapshot does not call config.Validate(); it only uses these modes to capture records.
        // Together with its built-in Constant/HorseRace/SingleBreathing/Flowing set this covers 0x81..0x88.
        return config;
    }

    internal static string UiModeName(K15LightingMode mode) => mode switch
    {
        K15LightingMode.Constant => "Constant",
        K15LightingMode.FlowingWater => "Flowing water",
        K15LightingMode.MonoWater => "Horse race (0x83; legacy mono_water)",
        K15LightingMode.SingleColorBreathing => "Single-color breathing",
        K15LightingMode.CycleBreathing => "Cycle breathing",
        K15LightingMode.TetrisBlocks => "Tetris blocks",
        K15LightingMode.Neon => "Neon",
        K15LightingMode.Ambilight => "Ambilight",
        K15LightingMode.Off => "Off",
        _ => mode.ToString()
    };

    internal static string ColorModel(K15LightingMode mode) => mode switch
    {
        K15LightingMode.Constant or K15LightingMode.SingleColorBreathing => "single_explicit_color",
        K15LightingMode.FlowingWater or K15LightingMode.CycleBreathing or K15LightingMode.TetrisBlocks => "palette_plus_mask",
        K15LightingMode.MonoWater or K15LightingMode.Neon or K15LightingMode.Ambilight => "oem_internal_or_unknown_palette",
        K15LightingMode.Off => "none",
        _ => "unknown"
    };

    private void Append(string eventName, object details)
    {
        var json = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "k15_lighting_lab",
            @event = eventName,
            details
        });
        lock (_logGate)
            File.AppendAllText(LogPath, json + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string Hex(IEnumerable<byte> bytes) =>
        string.Join(" ", bytes.Select(value => value.ToString("X2")));

    private static string ProfileName(byte slot) => slot switch
    {
        0 => "A",
        1 => "B",
        _ => $"{slot + 1}"
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LightingLabSession));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            var slot = _controller.ReadActiveSlot();
            if (_snapshots.TryGetValue(slot, out var snapshot))
            {
                _controller.Restore(snapshot);
                Append("baseline_restored", new { reason = "application_exit", onboardSlot = slot, profile = ProfileName(slot) });
            }
            else
            {
                Append("restore_skipped_no_snapshot", new { reason = "application_exit", onboardSlot = slot });
            }
        }
        catch (Exception ex)
        {
            Append("restore_failed", new { reason = "application_exit", exception = ex.GetType().FullName, message = ex.Message });
        }
        finally
        {
            _controller.Dispose();
            _disposed = true;
        }
    }
}
