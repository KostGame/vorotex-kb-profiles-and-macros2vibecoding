namespace Vorotex.K15.StatusLab;

internal sealed class K15RgbCanary : IAsyncDisposable
{
    private static readonly TimeSpan ProfilePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProfileFlashDuration = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<byte, K15HidLightingController.LightingSnapshot> _snapshots = new();

    private K15HidLightingController? _controller;
    private K15HidLightingController.LightingSnapshot? _snapshot;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private K15NormalizedState _desiredState = K15NormalizedState.Normal;
    private K15NormalizedState _appliedState = K15NormalizedState.Normal;
    private DateTimeOffset _profileFlashUntilUtc = DateTimeOffset.MinValue;
    private int _transportFailures;

    public bool Enabled { get; private set; }

    public event Action<string>? StatusChanged;

    public async Task EnableAsync(K15NormalizedState currentState)
    {
        await _gate.WaitAsync();
        try
        {
            if (Enabled)
                return;

            _controller = K15HidLightingController.Open();
            _snapshot = _controller.CaptureLightingSnapshot();
            _snapshots[_snapshot.OnboardSlot] = _snapshot;
            LogBaselineRepairIfNeeded(_snapshot, "enable");

            _desiredState = currentState;
            _appliedState = K15NormalizedState.Normal;
            _transportFailures = 0;
            _profileFlashUntilUtc = DateTimeOffset.MinValue;
            Enabled = true;

            Log("rgb_canary_enabled", new
            {
                onboardSlot = _snapshot.OnboardSlot,
                profile = ProfileName(_snapshot.OnboardSlot),
                originalMode = _snapshot.Header[0]
            });

            StatusChanged?.Invoke($"RGB: ON · profile {ProfileName(_snapshot.OnboardSlot)}");
            StartMonitorLocked();

            if (currentState != K15NormalizedState.Normal)
                ApplyDesiredLocked();
        }
        catch
        {
            _controller?.Dispose();
            _controller = null;
            _snapshot = null;
            _snapshots.Clear();
            Enabled = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyStateAsync(K15NormalizedState state)
    {
        await _gate.WaitAsync();
        try
        {
            _desiredState = state;
            if (!Enabled || _controller is null || _snapshot is null)
                return;

            try
            {
                var currentSlot = _controller.ReadActiveSlot();
                if (currentSlot != _snapshot.OnboardSlot)
                {
                    BeginProfileFlashLocked(currentSlot);
                    return;
                }

                if (IsProfileFlashActive())
                    return;

                ApplyDesiredLocked();
                _transportFailures = 0;
            }
            catch (K15HidLightingController.K15ProfileChangedException ex)
            {
                BeginProfileFlashLocked(ex.CurrentSlot);
            }
            catch (Exception ex) when (IsTransportFault(ex))
            {
                HandleTransportFaultLocked(ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void StartMonitorLocked()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitorTask = Task.Run(() => MonitorLoopAsync(token), token);
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProfilePollInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await _gate.WaitAsync(token);
            try
            {
                if (!Enabled || _controller is null || _snapshot is null)
                    continue;

                try
                {
                    var currentSlot = _controller.ReadActiveSlot();
                    if (currentSlot != _snapshot.OnboardSlot)
                    {
                        BeginProfileFlashLocked(currentSlot);
                        continue;
                    }

                    _transportFailures = 0;

                    if (_profileFlashUntilUtc != DateTimeOffset.MinValue &&
                        DateTimeOffset.UtcNow >= _profileFlashUntilUtc)
                    {
                        _profileFlashUntilUtc = DateTimeOffset.MinValue;
                        Log("rgb_profile_flash_completed", new
                        {
                            onboardSlot = _snapshot.OnboardSlot,
                            profile = ProfileName(_snapshot.OnboardSlot),
                            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
                        });
                        ApplyDesiredLocked();
                    }
                }
                catch (K15HidLightingController.K15ProfileChangedException ex)
                {
                    BeginProfileFlashLocked(ex.CurrentSlot);
                }
                catch (Exception ex) when (IsTransportFault(ex))
                {
                    HandleTransportFaultLocked(ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void BeginProfileFlashLocked(byte newSlot)
    {
        if (_controller is null)
            return;

        var previousSnapshot = _snapshot;

        // Let the keyboard finish loading the newly selected onboard profile before reading it.
        Thread.Sleep(180);
        var stableSlot = _controller.ReadActiveSlot();
        if (stableSlot != newSlot)
            newSlot = stableSlot;

        if (previousSnapshot is not null && previousSnapshot.OnboardSlot != newSlot)
        {
            RestorePreviousProfileAndReturnLocked(previousSnapshot, newSlot);
        }

        K15HidLightingController.LightingSnapshot newSnapshot;
        if (_snapshots.TryGetValue(newSlot, out var knownSnapshot))
        {
            newSnapshot = knownSnapshot;

            // A cached profile may have been left mid-overlay by an older Status Lab build.
            // Restore its exact accepted baseline before starting this switch indication.
            _controller.Restore(newSnapshot);
        }
        else
        {
            newSnapshot = _controller.CaptureLightingSnapshot();
            if (newSnapshot.OnboardSlot != newSlot)
                throw new InvalidOperationException("K15 profile did not remain stable while capturing its lighting baseline.");
            _snapshots[newSlot] = newSnapshot;
            LogBaselineRepairIfNeeded(newSnapshot, "profile_switch");
        }

        _snapshot = newSnapshot;
        _appliedState = K15NormalizedState.Normal;
        _profileFlashUntilUtc = DateTimeOffset.UtcNow + ProfileFlashDuration;
        _controller.ApplyProfileFlash(newSnapshot);
        _transportFailures = 0;

        Log("rgb_profile_flash_started", new
        {
            onboardSlot = newSlot,
            profile = ProfileName(newSlot),
            durationSeconds = ProfileFlashDuration.TotalSeconds,
            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
        });

        StatusChanged?.Invoke(
            $"RGB: PROFILE {ProfileName(newSlot)} · {ProfileFlashDuration.TotalSeconds:0}s → {JournalStateNormalizer.ToWireName(_desiredState)}");
    }

    private void RestorePreviousProfileAndReturnLocked(
        K15HidLightingController.LightingSnapshot previousSnapshot,
        byte returnSlot)
    {
        if (_controller is null || previousSnapshot.OnboardSlot == returnSlot)
            return;

        Exception? restoreFailure = null;
        try
        {
            _controller.SelectActiveSlot(previousSnapshot.OnboardSlot);
            _controller.Restore(previousSnapshot);
            Log("rgb_previous_profile_restored", new
            {
                onboardSlot = previousSnapshot.OnboardSlot,
                profile = ProfileName(previousSnapshot.OnboardSlot),
                returnSlot,
                returnProfile = ProfileName(returnSlot)
            });
        }
        catch (Exception ex)
        {
            restoreFailure = ex;
            Log("rgb_previous_profile_restore_failed", new
            {
                onboardSlot = previousSnapshot.OnboardSlot,
                profile = ProfileName(previousSnapshot.OnboardSlot),
                returnSlot,
                exception = ex.GetType().FullName,
                hresult = ex.HResult,
                message = ex.Message
            });
        }
        finally
        {
            // Never leave the user's keyboard on the temporary cleanup slot.
            _controller.SelectActiveSlot(returnSlot);
            Thread.Sleep(90);
        }

        if (restoreFailure is not null)
            throw new IOException("Could not restore the previous K15 profile overlay before switching.", restoreFailure);
    }

    private void ApplyDesiredLocked()
    {
        if (_controller is null || _snapshot is null)
            return;

        if (_desiredState == K15NormalizedState.Normal)
        {
            _controller.Restore(_snapshot);
            if (_appliedState != K15NormalizedState.Normal)
            {
                Log("rgb_restored", new
                {
                    reason = "normalized_state_normal",
                    onboardSlot = _snapshot.OnboardSlot,
                    profile = ProfileName(_snapshot.OnboardSlot)
                });
            }
        }
        else
        {
            _controller.ApplyState(_snapshot, _desiredState);
            Log("rgb_state_applied", new
            {
                state = JournalStateNormalizer.ToWireName(_desiredState),
                onboardSlot = _snapshot.OnboardSlot,
                profile = ProfileName(_snapshot.OnboardSlot)
            });
        }

        _appliedState = _desiredState;
        StatusChanged?.Invoke(
            $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} · profile {ProfileName(_snapshot.OnboardSlot)}");
    }

    private void HandleTransportFaultLocked(Exception ex)
    {
        _transportFailures++;
        Log("rgb_transport_retry", new
        {
            attempt = _transportFailures,
            exception = ex.GetType().FullName,
            hresult = ex.HResult,
            message = ex.Message,
            desiredState = JournalStateNormalizer.ToWireName(_desiredState),
            onboardSlot = _snapshot?.OnboardSlot
        });

        StatusChanged?.Invoke($"RGB: RETRYING · {ShortTransportMessage(ex)}");

        if (_transportFailures < 2)
            return;

        try
        {
            _controller?.Dispose();
            _controller = K15HidLightingController.Open();
            var currentSlot = _controller.ReadActiveSlot();
            _transportFailures = 0;

            if (_snapshot is null || currentSlot != _snapshot.OnboardSlot)
            {
                BeginProfileFlashLocked(currentSlot);
                return;
            }

            Log("rgb_transport_reconnected", new
            {
                onboardSlot = currentSlot,
                profile = ProfileName(currentSlot)
            });
            StatusChanged?.Invoke($"RGB: RECONNECTED · profile {ProfileName(currentSlot)}");

            if (!IsProfileFlashActive())
                ApplyDesiredLocked();
        }
        catch (Exception retryEx) when (IsTransportFault(retryEx))
        {
            Log("rgb_transport_reconnect_pending", new
            {
                exception = retryEx.GetType().FullName,
                hresult = retryEx.HResult,
                message = retryEx.Message
            });
            StatusChanged?.Invoke($"RGB: RETRYING · {ShortTransportMessage(retryEx)}");
        }
    }

    private bool IsProfileFlashActive() =>
        _profileFlashUntilUtc != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow < _profileFlashUntilUtc;

    private static bool IsTransportFault(Exception ex) =>
        ex is TimeoutException or IOException or InvalidDataException or System.ComponentModel.Win32Exception;

    private static string ShortTransportMessage(Exception ex) => ex switch
    {
        TimeoutException => "HID transition/timeout",
        InvalidDataException => "HID readback mismatch",
        _ => $"0x{ex.HResult:X8}"
    };

    private static string ProfileName(byte slot) => slot switch
    {
        0 => "A",
        1 => "B",
        _ => $"{slot + 1}"
    };

    private static void LogBaselineRepairIfNeeded(
        K15HidLightingController.LightingSnapshot snapshot,
        string context)
    {
        if (!snapshot.BaselineModeRepaired)
            return;

        Log("rgb_stale_profile_mode_repaired", new
        {
            onboardSlot = snapshot.OnboardSlot,
            profile = ProfileName(snapshot.OnboardSlot),
            context,
            repairedTo = "CONSTANT"
        });
    }

    public async Task DisableAsync(string reason = "manual_disable")
    {
        var monitorCts = _monitorCts;
        var monitorTask = _monitorTask;
        monitorCts?.Cancel();

        await _gate.WaitAsync();
        try
        {
            if (!Enabled)
                return;

            try
            {
                if (_controller is not null)
                {
                    var currentSlot = _controller.ReadActiveSlot();
                    if (!_snapshots.TryGetValue(currentSlot, out var currentSnapshot))
                    {
                        currentSnapshot = _controller.CaptureLightingSnapshot();
                        _snapshots[currentSlot] = currentSnapshot;
                        LogBaselineRepairIfNeeded(currentSnapshot, "disable");
                    }

                    _snapshot = currentSnapshot;
                    _controller.Restore(currentSnapshot);
                    Log("rgb_restored", new
                    {
                        reason,
                        onboardSlot = currentSnapshot.OnboardSlot,
                        profile = ProfileName(currentSnapshot.OnboardSlot)
                    });
                }
            }
            catch (Exception ex)
            {
                // Disabling a tray feature must never crash the WinForms process. We still dispose
                // the HID handle and surface the failed restore in the journal/status for recovery.
                Log("rgb_restore_failed_on_disable", new
                {
                    reason,
                    exception = ex.GetType().FullName,
                    hresult = ex.HResult,
                    message = ex.Message
                });
                StatusChanged?.Invoke($"RGB: OFF · restore failed ({ShortTransportMessage(ex)})");
            }
            finally
            {
                _controller?.Dispose();
                _controller = null;
                _snapshot = null;
                _snapshots.Clear();
                _desiredState = K15NormalizedState.Normal;
                _appliedState = K15NormalizedState.Normal;
                _profileFlashUntilUtc = DateTimeOffset.MinValue;
                _transportFailures = 0;
                Enabled = false;
                StatusChanged?.Invoke("RGB: OFF");
                Log("rgb_canary_disabled", new { reason });
            }
        }
        finally
        {
            _gate.Release();
        }

        if (monitorTask is not null)
        {
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
    }

    private static void Log(string eventName, object? details = null)
    {
        EventJournal.Append(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            source = "k15_rgb",
            @event = eventName,
            details
        });
    }

    public async ValueTask DisposeAsync()
    {
        await DisableAsync("application_exit");
        _gate.Dispose();
    }
}
