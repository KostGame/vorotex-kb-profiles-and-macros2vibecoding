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

    public K15RgbCanary(StatusLabConfig config) => _config = config;

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
            ResetVisualStateLocked();
            Enabled = true;

            Log("rgb_canary_enabled", new
            {
                onboardSlot = _snapshot.OnboardSlot,
                profile = ProfileName(_snapshot.OnboardSlot),
                exactBaselineMode = _snapshot.Header[0],
                configPath = StatusLabConfig.FilePath,
                wireColorOrder = _config.WireColorOrder.ToString(),
                hardwareProfileSelectionPolicy = "observe_only"
            });
            StatusChanged?.Invoke($"RGB: ON · profile {ProfileName(_snapshot.OnboardSlot)}");
            StartMonitorLocked();

            if (_config.ActivationSignal.Enabled && _config.ActivationSignal.DurationSeconds > 0)
                BeginOverlayLocked(_config.ActivationSignal, "ACTIVATION", _config.ActivationSignal.DurationSeconds, "rgb_activation_signal_started");
            else
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
            SetDesiredStateLocked(state);
            if (!Enabled || _controller is null || _snapshot is null)
                return;

            try
            {
                var currentSlot = _controller.ReadActiveSlot();
                if (currentSlot != _snapshot.OnboardSlot)
                {
                    AdoptActiveProfileLocked(currentSlot, startProfileOverlay: true);
                    return;
                }

                if (!IsOverlayActive())
                    ApplyDesiredLocked();
                _transportFailures = 0;
            }
            catch (K15HidLightingController.K15ProfileChangedException ex)
            {
                AdoptActiveProfileLocked(ex.CurrentSlot, startProfileOverlay: true);
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

    public async Task TestEffectAsync(K15LightingMode mode)
    {
        await _gate.WaitAsync();
        try
        {
            if (!Enabled || _controller is null || _snapshot is null)
                throw new InvalidOperationException("Enable K15 RGB canary before running Effect Lab.");

            var currentSlot = _controller.ReadActiveSlot();
            if (currentSlot != _snapshot.OnboardSlot)
                AdoptActiveProfileLocked(currentSlot, startProfileOverlay: false);

            var test = new LightingEffectConfig
            {
                Enabled = true,
                Mode = mode,
                Brightness = 5,
                Speed = 4,
                Direction = 0,
                DurationSeconds = _config.EffectLabDurationSeconds
            };
            BeginOverlayLocked(test, $"EFFECT_TEST_{StatusLabConfig.ModeName(mode)}",
                _config.EffectLabDurationSeconds, "rgb_effect_test_started");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreCurrentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!Enabled || _controller is null || _snapshot is null)
                return;

            var currentSlot = _controller.ReadActiveSlot();
            if (currentSlot != _snapshot.OnboardSlot)
                AdoptActiveProfileLocked(currentSlot, startProfileOverlay: false);

            ClearOverlayLocked();
            _controller.Restore(_snapshot);
            _appliedState = K15NormalizedState.Normal;
            if (_desiredState != K15NormalizedState.Normal)
                _expiredState = _desiredState;
            StatusChanged?.Invoke($"RGB: baseline restored · profile {ProfileName(_snapshot.OnboardSlot)}");
            Log("rgb_effect_test_restored", new { onboardSlot = _snapshot.OnboardSlot });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetDesiredStateLocked(K15NormalizedState state)
    {
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

    private void ResetVisualStateLocked()
    {
        _appliedState = K15NormalizedState.Normal;
        _transportFailures = 0;
        ClearOverlayLocked();
        _expiredState = null;
    }

    private void ClearOverlayLocked()
    {
        _overlayUntilUtc = DateTimeOffset.MinValue;
        _overlayKind = string.Empty;
        _overlayEffect = null;
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
            try { await Task.Delay(ProfilePollInterval, token); }
            catch (OperationCanceledException) { break; }

            try { await _gate.WaitAsync(token); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (!Enabled || _controller is null || _snapshot is null)
                    continue;

                try
                {
                    var currentSlot = _controller.ReadActiveSlot();
                    if (currentSlot != _snapshot.OnboardSlot)
                    {
                        AdoptActiveProfileLocked(currentSlot, startProfileOverlay: true);
                        continue;
                    }

                    _transportFailures = 0;
                    var now = DateTimeOffset.UtcNow;
                    if (_overlayUntilUtc != DateTimeOffset.MinValue && now >= _overlayUntilUtc)
                    {
                        var completedKind = _overlayKind;
                        ClearOverlayLocked();
                        Log("rgb_overlay_completed", new
                        {
                            kind = completedKind,
                            onboardSlot = _snapshot.OnboardSlot,
                            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
                        });

                        if (completedKind.StartsWith("EFFECT_TEST_", StringComparison.Ordinal))
                        {
                            _controller.Restore(_snapshot);
                            _appliedState = K15NormalizedState.Normal;
                            if (_desiredState != K15NormalizedState.Normal)
                                _expiredState = _desiredState;
                            StatusChanged?.Invoke($"RGB: Effect Lab restored baseline · profile {ProfileName(_snapshot.OnboardSlot)}");
                        }
                        else
                        {
                            ApplyDesiredLocked();
                        }
                        continue;
                    }

                    if (!IsOverlayActive() && _stateVisualUntilUtc is DateTimeOffset until &&
                        now >= until && _desiredState != K15NormalizedState.Normal && _expiredState != _desiredState)
                    {
                        _expiredState = _desiredState;
                        _controller.Restore(_snapshot);
                        _appliedState = K15NormalizedState.Normal;
                        StatusChanged?.Invoke($"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} expired · baseline {ProfileName(_snapshot.OnboardSlot)}");
                    }
                }
                catch (K15HidLightingController.K15ProfileChangedException ex)
                {
                    AdoptActiveProfileLocked(ex.CurrentSlot, startProfileOverlay: true);
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

    private void AdoptActiveProfileLocked(byte observedSlot, bool startProfileOverlay)
    {
        if (_controller is null)
            return;

        Thread.Sleep(180);
        var stableSlot = _controller.ReadActiveSlot();
        if (stableSlot != observedSlot)
            observedSlot = stableSlot;

        if (_snapshot?.OnboardSlot == observedSlot)
            return;

        if (_snapshots.TryGetValue(observedSlot, out var knownSnapshot))
        {
            _snapshot = knownSnapshot;
        }
        else
        {
            var captured = _controller.PrepareProfileSnapshot(_config);
            if (captured.OnboardSlot != observedSlot)
                throw new TimeoutException("K15 profile did not remain stable while capturing exact baseline.");
            _snapshots[observedSlot] = captured;
            _snapshot = captured;
        }

        _appliedState = K15NormalizedState.Normal;
        _expiredState = null;
        ClearOverlayLocked();

        Log("rgb_profile_observed", new
        {
            onboardSlot = observedSlot,
            profile = ProfileName(observedSlot),
            programmaticProfileSelection = false,
            cachedSnapshot = _snapshots.ContainsKey(observedSlot)
        });

        if (startProfileOverlay && _config.ProfileSwitch.Enabled && _config.ProfileSwitch.DurationSeconds > 0)
        {
            BeginOverlayLocked(_config.ProfileSwitch, $"PROFILE_{ProfileName(observedSlot)}",
                _config.ProfileSwitch.DurationSeconds, "rgb_profile_overlay_started");
            return;
        }

        ApplyDesiredLocked();
    }

    private void BeginOverlayLocked(LightingEffectConfig source, string kind,
        double durationSeconds, string eventName)
    {
        if (_controller is null || _snapshot is null)
            return;

        var rendered = _config.RenderForProfile(_snapshot.OnboardSlot, source);
        _overlayEffect = rendered;
        _overlayKind = kind;
        _overlayUntilUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(durationSeconds);
        _controller.ApplyEffect(_snapshot, rendered, _config.WireColorOrder, kind);
        _transportFailures = 0;

        Log(eventName, new
        {
            kind,
            onboardSlot = _snapshot.OnboardSlot,
            profile = ProfileName(_snapshot.OnboardSlot),
            mode = rendered.Mode.ToString(),
            color = rendered.Colors.Single(),
            brightness = rendered.Brightness,
            speed = rendered.Speed,
            durationSeconds,
            directActiveProfilePath = true,
            resumeState = JournalStateNormalizer.ToWireName(_desiredState)
        });
        StatusChanged?.Invoke($"RGB: {kind} · {durationSeconds:0.#}s · profile {ProfileName(_snapshot.OnboardSlot)}");
    }

    private void ApplyDesiredLocked()
    {
        if (_controller is null || _snapshot is null)
            return;

        if (_desiredState == K15NormalizedState.Normal || _expiredState == _desiredState)
        {
            _controller.Restore(_snapshot);
            _appliedState = K15NormalizedState.Normal;
            StatusChanged?.Invoke($"RGB: NORMAL · exact baseline {ProfileName(_snapshot.OnboardSlot)}");
            return;
        }

        var source = _config.GetState(_desiredState);
        if (!source.Enabled)
        {
            _controller.Restore(_snapshot);
            _appliedState = K15NormalizedState.Normal;
            return;
        }

        var rendered = _config.RenderForProfile(_snapshot.OnboardSlot, source);
        _controller.ApplyEffect(_snapshot, rendered, _config.WireColorOrder,
            JournalStateNormalizer.ToWireName(_desiredState));
        _appliedState = _desiredState;

        Log("rgb_state_applied", new
        {
            state = JournalStateNormalizer.ToWireName(_desiredState),
            onboardSlot = _snapshot.OnboardSlot,
            mode = rendered.Mode.ToString(),
            color = rendered.Colors.Single(),
            brightness = rendered.Brightness,
            speed = rendered.Speed
        });
        StatusChanged?.Invoke($"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} · {rendered.Mode} · profile {ProfileName(_snapshot.OnboardSlot)}");
    }

    private void HandleTransportFaultLocked(Exception ex)
    {
        _transportFailures++;
        Log("rgb_transport_retry", new
        {
            attempt = _transportFailures,
            exception = ex.GetType().FullName,
            hresult = ex.HResult,
            message = ex.Message
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
                AdoptActiveProfileLocked(currentSlot, startProfileOverlay: true);
                return;
            }

            StatusChanged?.Invoke($"RGB: RECONNECTED · profile {ProfileName(currentSlot)}");
            Log("rgb_transport_reconnected", new { onboardSlot = currentSlot });
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
                message = retryEx.Message
            });
            StatusChanged?.Invoke($"RGB: RETRYING · {ShortTransportMessage(retryEx)}");
        }
    }

    private bool IsOverlayActive() =>
        _overlayUntilUtc != DateTimeOffset.MinValue && DateTimeOffset.UtcNow < _overlayUntilUtc;

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
                    if (_snapshots.TryGetValue(currentSlot, out var currentSnapshot))
                    {
                        _snapshot = currentSnapshot;
                        _controller.Restore(currentSnapshot);
                        Log("rgb_active_profile_restored_on_disable", new
                        {
                            reason,
                            onboardSlot = currentSlot,
                            profile = ProfileName(currentSlot)
                        });
                    }

                    var deferred = _snapshots.Keys.Where(slot => slot != currentSlot).OrderBy(slot => slot).ToArray();
                    if (deferred.Length > 0)
                        Log("rgb_inactive_profile_restore_deferred", new { reason, activeSlot = currentSlot, deferredSlots = deferred });
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
                StatusChanged?.Invoke($"RGB: OFF · restore incomplete ({ShortTransportMessage(ex)})");
            }
            finally
            {
                _controller?.Dispose();
                _controller = null;
                _snapshot = null;
                _snapshots.Clear();
                _desiredState = K15NormalizedState.Normal;
                _stateVisualUntilUtc = null;
                ResetVisualStateLocked();
                Enabled = false;
                StatusChanged?.Invoke("RGB: OFF");
                Log("rgb_canary_disabled", new { reason, programmaticProfileSelection = false });
            }
        }
        finally
        {
            _gate.Release();
        }

        if (monitorTask is not null)
        {
            try { await monitorTask; }
            catch (OperationCanceledException) { }
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
