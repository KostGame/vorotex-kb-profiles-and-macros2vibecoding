namespace Vorotex.K15.StatusLab;

internal sealed class K15RgbCanary : IAsyncDisposable
{
    private static readonly TimeSpan ProfilePollInterval = TimeSpan.FromMilliseconds(500);

    private readonly StatusLabConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<byte, K15HidLightingController.LightingSnapshot> _snapshots = new();

    private K15HidLightingController? _controller;
    private K15HidLightingController.LightingSnapshot? _snapshot;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private K15NormalizedState _desiredState = K15NormalizedState.Normal;
    private K15NormalizedState _appliedState = K15NormalizedState.Normal;
    private DateTimeOffset _overlayUntilUtc = DateTimeOffset.MinValue;
    private string _overlayKind = string.Empty;
    private LightingEffectConfig? _overlayEffect;
    private DateTimeOffset? _stateVisualUntilUtc;
    private K15NormalizedState? _expiredState;
    private int _transportFailures;

    public K15RgbCanary(StatusLabConfig config)
    {
        _config = config;
    }

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
            _snapshot = _controller.PrepareProfileSnapshot(_config);
            _snapshots[_snapshot.OnboardSlot] = _snapshot;

            SetDesiredStateLocked(currentState);
            _appliedState = K15NormalizedState.Normal;
            _transportFailures = 0;
            _overlayUntilUtc = DateTimeOffset.MinValue;
            _overlayKind = string.Empty;
            _overlayEffect = null;
            Enabled = true;

            Log("rgb_canary_enabled", new
            {
                onboardSlot = _snapshot.OnboardSlot,
                profile = ProfileName(_snapshot.OnboardSlot),
                baselineMode = _snapshot.Header[0],
                configPath = StatusLabConfig.FilePath,
                wireColorOrder = _config.WireColorOrder.ToString()
            });

            StatusChanged?.Invoke($"RGB: ON · profile {ProfileName(_snapshot.OnboardSlot)}");
            StartMonitorLocked();

            if (_config.ActivationSignal.Enabled && _config.ActivationSignal.DurationSeconds > 0)
            {
                BeginOverlayLocked(
                    _config.ActivationSignal,
                    "ACTIVATION",
                    _config.ActivationSignal.DurationSeconds,
                    "rgb_activation_signal_started");
            }
            else
            {
                ApplyDesiredLocked();
            }
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
            SetDesiredStateLocked(state);
            if (!Enabled || _controller is null || _snapshot is null)
                return;

            try
            {
                var currentSlot = _controller.ReadActiveSlot();
                if (currentSlot != _snapshot.OnboardSlot)
                {
                    BeginProfileOverlayLocked(currentSlot);
                    return;
                }

                if (IsOverlayActive())
                    return;

                ApplyDesiredLocked();
                _transportFailures = 0;
            }
            catch (K15HidLightingController.K15ProfileChangedException ex)
            {
                BeginProfileOverlayLocked(ex.CurrentSlot);
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

    private void SetDesiredStateLocked(K15NormalizedState state)
    {
        if (_desiredState == state && _stateVisualUntilUtc is not null)
            return;

        if (_desiredState != state)
            _expiredState = null;

        _desiredState = state;

        if (state == K15NormalizedState.Normal)
        {
            _stateVisualUntilUtc = null;
            _expiredState = null;
            return;
        }

        var effect = _config.GetState(state);
        _stateVisualUntilUtc = effect.DurationSeconds > 0
            ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(effect.DurationSeconds)
            : null;
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
                        BeginProfileOverlayLocked(currentSlot);
                        continue;
                    }

                    _transportFailures = 0;
                    var now = DateTimeOffset.UtcNow;

                    if (_overlayUntilUtc != DateTimeOffset.MinValue && now >= _overlayUntilUtc)
                    {
                        var completedKind = _overlayKind;
                        _overlayUntilUtc = DateTimeOffset.MinValue;
                        _overlayKind = string.Empty;
                        _overlayEffect = null;

                        Log("rgb_overlay_completed", new
                        {
                            kind = completedKind,
                            onboardSlot = _snapshot.OnboardSlot,
                            profile = ProfileName(_snapshot.OnboardSlot),
                            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
                        });
                        ApplyDesiredLocked();
                        continue;
                    }

                    if (!IsOverlayActive() &&
                        _stateVisualUntilUtc is DateTimeOffset stateUntil &&
                        now >= stateUntil &&
                        _desiredState != K15NormalizedState.Normal &&
                        _expiredState != _desiredState)
                    {
                        _expiredState = _desiredState;
                        _controller.Restore(_snapshot);
                        _appliedState = K15NormalizedState.Normal;
                        Log("rgb_state_effect_expired", new
                        {
                            state = JournalStateNormalizer.ToWireName(_desiredState),
                            onboardSlot = _snapshot.OnboardSlot,
                            profile = ProfileName(_snapshot.OnboardSlot)
                        });
                        StatusChanged?.Invoke(
                            $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} expired · baseline {ProfileName(_snapshot.OnboardSlot)}");
                    }
                }
                catch (K15HidLightingController.K15ProfileChangedException ex)
                {
                    BeginProfileOverlayLocked(ex.CurrentSlot);
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

    private void BeginProfileOverlayLocked(byte newSlot)
    {
        if (_controller is null)
            return;

        var previousSnapshot = _snapshot;

        Thread.Sleep(180);
        var stableSlot = _controller.ReadActiveSlot();
        if (stableSlot != newSlot)
            newSlot = stableSlot;

        if (previousSnapshot is not null && previousSnapshot.OnboardSlot != newSlot)
            RestorePreviousProfileAndReturnLocked(previousSnapshot, newSlot);

        K15HidLightingController.LightingSnapshot newSnapshot;
        if (_snapshots.TryGetValue(newSlot, out var knownSnapshot))
        {
            newSnapshot = knownSnapshot;
            _controller.Restore(newSnapshot);
        }
        else
        {
            newSnapshot = _controller.PrepareProfileSnapshot(_config);
            if (newSnapshot.OnboardSlot != newSlot)
                throw new InvalidOperationException("K15 profile did not remain stable while preparing its baseline.");
            _snapshots[newSlot] = newSnapshot;
        }

        _snapshot = newSnapshot;
        _appliedState = K15NormalizedState.Normal;

        var switchEffect = _config.GetProfile(newSlot).SwitchSignal;
        if (!switchEffect.Enabled || switchEffect.DurationSeconds <= 0)
        {
            ApplyDesiredLocked();
            return;
        }

        BeginOverlayLocked(
            switchEffect,
            $"PROFILE_{ProfileName(newSlot)}",
            switchEffect.DurationSeconds,
            "rgb_profile_flash_started");
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
            _controller.SelectActiveSlot(returnSlot);
            Thread.Sleep(90);
        }

        if (restoreFailure is not null)
            throw new IOException("Could not restore the previous K15 profile overlay before switching.", restoreFailure);
    }

    private void BeginOverlayLocked(
        LightingEffectConfig effect,
        string kind,
        double durationSeconds,
        string eventName)
    {
        if (_controller is null || _snapshot is null)
            return;

        _overlayEffect = effect.Clone();
        _overlayKind = kind;
        _overlayUntilUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(durationSeconds);
        _controller.ApplyEffect(_snapshot, _overlayEffect, _config.WireColorOrder, kind);
        _transportFailures = 0;

        Log(eventName, new
        {
            kind,
            onboardSlot = _snapshot.OnboardSlot,
            profile = ProfileName(_snapshot.OnboardSlot),
            mode = effect.Mode.ToString(),
            brightness = effect.Brightness,
            speed = effect.Speed,
            durationSeconds,
            colors = effect.Colors,
            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
        });

        StatusChanged?.Invoke(
            $"RGB: {kind} · {durationSeconds:0.#}s → {JournalStateNormalizer.ToWireName(_desiredState)}");
    }

    private void ApplyDesiredLocked()
    {
        if (_controller is null || _snapshot is null)
            return;

        if (_desiredState == K15NormalizedState.Normal || _expiredState == _desiredState)
        {
            _controller.Restore(_snapshot);
            if (_appliedState != K15NormalizedState.Normal)
            {
                Log("rgb_restored", new
                {
                    reason = _desiredState == K15NormalizedState.Normal
                        ? "normalized_state_normal"
                        : "state_effect_expired",
                    onboardSlot = _snapshot.OnboardSlot,
                    profile = ProfileName(_snapshot.OnboardSlot)
                });
            }
            _appliedState = K15NormalizedState.Normal;
            StatusChanged?.Invoke($"RGB: NORMAL · profile {ProfileName(_snapshot.OnboardSlot)}");
            return;
        }

        var effect = _config.GetState(_desiredState);
        if (!effect.Enabled)
        {
            _controller.Restore(_snapshot);
            _appliedState = K15NormalizedState.Normal;
            StatusChanged?.Invoke(
                $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} disabled · baseline {ProfileName(_snapshot.OnboardSlot)}");
            return;
        }

        _controller.ApplyEffect(
            _snapshot,
            effect,
            _config.WireColorOrder,
            JournalStateNormalizer.ToWireName(_desiredState));

        Log("rgb_state_applied", new
        {
            state = JournalStateNormalizer.ToWireName(_desiredState),
            onboardSlot = _snapshot.OnboardSlot,
            profile = ProfileName(_snapshot.OnboardSlot),
            mode = effect.Mode.ToString(),
            brightness = effect.Brightness,
            speed = effect.Speed,
            durationSeconds = effect.DurationSeconds,
            colors = effect.Colors
        });

        _appliedState = _desiredState;
        StatusChanged?.Invoke(
            $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} · {effect.Mode} · profile {ProfileName(_snapshot.OnboardSlot)}");
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
                BeginProfileOverlayLocked(currentSlot);
                return;
            }

            Log("rgb_transport_reconnected", new
            {
                onboardSlot = currentSlot,
                profile = ProfileName(currentSlot)
            });
            StatusChanged?.Invoke($"RGB: RECONNECTED · profile {ProfileName(currentSlot)}");

            if (IsOverlayActive() && _overlayEffect is not null)
                _controller.ApplyEffect(_snapshot, _overlayEffect, _config.WireColorOrder, _overlayKind);
            else
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

    private bool IsOverlayActive() =>
        _overlayUntilUtc != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow < _overlayUntilUtc;

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
                        currentSnapshot = _controller.PrepareProfileSnapshot(_config);
                        _snapshots[currentSlot] = currentSnapshot;
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
                _overlayUntilUtc = DateTimeOffset.MinValue;
                _overlayKind = string.Empty;
                _overlayEffect = null;
                _stateVisualUntilUtc = null;
                _expiredState = null;
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
