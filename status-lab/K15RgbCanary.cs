namespace Vorotex.K15.StatusLab;

internal sealed class K15RgbCanary : IAsyncDisposable
{
    private static readonly TimeSpan ProfilePollInterval = TimeSpan.FromMilliseconds(500);

    private readonly StatusLabConfig _config;
    private readonly K15DeviceManager _deviceManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<byte, K15HidLightingController.LightingSnapshot> _snapshots = new();
    private readonly Dictionary<byte, K15HidLightingController.LightingSnapshot> _pendingRestores = new();
    private readonly ProfileColorHintTracker _profileHint = new();
    private int _disposed;

    private K15HidLightingController? _controller;
    private long? _bindingGeneration;
    private K15HidLightingController.LightingSnapshot? _snapshot;
    private readonly K15ProfileMonitorLifecycle _monitorLifecycle = new();
    private K15NormalizedState _desiredState = K15NormalizedState.Normal;
    private K15NormalizedState _appliedState = K15NormalizedState.Normal;
    private DateTimeOffset _overlayUntilUtc = DateTimeOffset.MinValue;
    private string _overlayKind = string.Empty;
    private LightingEffectConfig? _overlayEffect;
    private DateTimeOffset? _stateVisualUntilUtc;
    private K15NormalizedState? _expiredState;
    private int _transportFailures;

    public K15RgbCanary(StatusLabConfig config, K15DeviceManager deviceManager)
    {
        _config = config;
        _deviceManager = deviceManager;
        _deviceManager.StateChanged += OnDeviceStateChanged;
        _deviceManager.ActiveSlotObserved += PublishActiveSlot;
        StartMonitorLocked();
    }

    public K15RgbCanary(StatusLabConfig config)
        : this(config, new K15DeviceManager(Path.Combine(EventJournal.DirectoryPath, "preferred-device.json")))
    {
    }

    public bool Enabled { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<ProfileColorHint>? ProfileHintChanged;
    public ProfileColorHint CurrentProfileHint => _profileHint.Current(DateTimeOffset.UtcNow);

    public async Task EnableAsync(K15NormalizedState currentState)
    {
        await _gate.WaitAsync();
        try
        {
            if (Enabled)
                return;

            if (_deviceManager.ConnectionState != K15DeviceConnectionState.Connected ||
                _deviceManager.Controller is null)
                throw new InvalidOperationException("Connect a verified K15 device before enabling RGB.");
            _controller = _deviceManager.Controller;
            _bindingGeneration = _deviceManager.ConnectionGeneration;
            if (!IsCurrentBindingLocked())
                throw new InvalidOperationException("K15 device binding changed while enabling RGB.");
            var currentSlot = _controller.ReadActiveSlot();
            PublishActiveSlot(currentSlot, DateTimeOffset.UtcNow);
            RestorePendingForSlotLocked(_controller, currentSlot, "rgb_enable");
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
                managedNormal = _snapshot.CanonicalNormal.Enabled,
                configPath = StatusLabConfig.FilePath,
                wireColorOrder = _config.WireColorOrder.ToString(),
                pendingRestoreSlots = _pendingRestores.Keys.OrderBy(slot => slot).ToArray(),
                hardwareProfileSelectionPolicy = "observe_only"
            });
            StatusChanged?.Invoke($"RGB: ON · profile {ProfileName(_snapshot.OnboardSlot)}");
            StartMonitorLocked();

            if (_config.SelectEnablePresentation(currentState) == RgbEnablePresentation.Activation)
            {
                BeginOverlayLocked(_config.ActivationSignal, "ACTIVATION",
                    _config.ActivationSignal.DurationSeconds, "rgb_activation_signal_started");
            }
            else
            {
                ApplyDesiredLocked();
            }
        }
        catch
        {
            _controller = null;
            _bindingGeneration = null;
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

    public async Task ApplyStateAsync(K15NormalizedState state, StateTransition? transition = null)
    {
        await _gate.WaitAsync();
        try
        {
            SetDesiredStateLocked(state);
            if (!Enabled || _controller is null || _snapshot is null || !IsCurrentBindingLocked())
                return;

            try
            {
                var currentSlot = _controller.ReadActiveSlot();
                if (currentSlot != _snapshot.OnboardSlot)
                {
                    AdoptActiveProfileLocked(currentSlot, startProfileOverlay: true);
                    return;
                }

                if (state == K15NormalizedState.Normal &&
                    !_overlayKind.StartsWith("EFFECT_TEST_", StringComparison.Ordinal))
                {
                    ClearOverlayLocked();
                    ApplyDesiredLocked();
                    return;
                }

                if (transition?.Reason == "codex_stop" &&
                    _config.StopSignal.Enabled && _config.StopSignal.DurationSeconds > 0)
                {
                    BeginOverlayLocked(_config.StopSignal, "STOP_SIGNAL",
                        _config.StopSignal.DurationSeconds, "rgb_stop_signal_started");
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
            if (!Enabled || _controller is null || _snapshot is null || !IsCurrentBindingLocked())
                throw new InvalidOperationException("Enable K15 RGB indication before running Effect Lab.");

            var currentSlot = _controller.ReadActiveSlot();
            if (currentSlot != _snapshot.OnboardSlot)
                AdoptActiveProfileLocked(currentSlot, startProfileOverlay: false);

            var test = new LightingEffectConfig
            {
                Enabled = true,
                Mode = mode,
                Palette = PaletteSource.Profile,
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
            if (Enabled && _controller is not null && _snapshot is not null && IsCurrentBindingLocked())
            {
                var currentSlot = _controller.ReadActiveSlot();
                if (currentSlot != _snapshot.OnboardSlot)
                    AdoptActiveProfileLocked(currentSlot, startProfileOverlay: false);

                ClearOverlayLocked();
                _controller.Restore(_snapshot);
                _pendingRestores.Remove(_snapshot.OnboardSlot);
                _appliedState = K15NormalizedState.Normal;
                if (_desiredState != K15NormalizedState.Normal)
                    _expiredState = _desiredState;
                var policy = _snapshot.CanonicalNormal.Enabled ? "canonical NORMAL" : "exact baseline";
                StatusChanged?.Invoke($"RGB: {policy} restored · profile {ProfileName(_snapshot.OnboardSlot)}");
                Log("rgb_manual_baseline_restored", new
                {
                    onboardSlot = _snapshot.OnboardSlot,
                    trackingEnabled = true,
                    managedNormal = _snapshot.CanonicalNormal.Enabled
                });
                return;
            }

            var controller = _deviceManager.Controller;
            if (_deviceManager.ConnectionState != K15DeviceConnectionState.Connected || controller is null)
            {
                StatusChanged?.Invoke("RGB: OFF · device unavailable");
                return;
            }
            var captured = controller.PrepareProfileSnapshot(_config);
            var slot = captured.OnboardSlot;
            if (captured.CanonicalNormal.Enabled)
            {
                controller.Restore(captured);
                _pendingRestores.Remove(slot);
                StatusChanged?.Invoke($"RGB: OFF · canonical NORMAL repaired · profile {ProfileName(slot)}");
                Log("rgb_manual_baseline_restored", new
                {
                    onboardSlot = slot,
                    trackingEnabled = false,
                    managedNormal = true,
                    trigger = "manual_repair_while_disabled"
                });
                return;
            }

            if (!_pendingRestores.TryGetValue(slot, out var pending))
            {
                StatusChanged?.Invoke($"RGB: OFF · no pending exact restore for profile {ProfileName(slot)}");
                return;
            }

            controller.Restore(pending);
            _pendingRestores.Remove(slot);
            StatusChanged?.Invoke($"RGB: OFF · exact baseline recovered · profile {ProfileName(slot)}");
            Log("rgb_pending_baseline_restored", new
            {
                onboardSlot = slot,
                profile = ProfileName(slot),
                trigger = "manual_restore_while_disabled",
                managedNormal = false,
                remainingSlots = _pendingRestores.Keys.OrderBy(value => value).ToArray()
            });
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
        _monitorLifecycle.Start(MonitorLoopAsync);
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
                var observedController = Enabled ? _controller : _deviceManager.Controller;
                if (_deviceManager.ConnectionState != K15DeviceConnectionState.Connected || observedController is null)
                    continue;
                if (Enabled && !IsCurrentBindingLocked())
                {
                    InvalidateBindingLocked("monitor_stale_binding");
                    continue;
                }

                try
                {
                    var currentSlot = observedController.ReadActiveSlot();
                    PublishActiveSlot(currentSlot, DateTimeOffset.UtcNow);
                    if (!Enabled)
                    {
                        // RGB-OFF monitoring is passive except for fulfilling an exact deferred
                        // restore obligation for the slot the owner has physically selected.
                        RestorePendingForSlotLocked(observedController, currentSlot, "profile_observed_while_disabled");
                        continue;
                    }

                    if (_controller is null || _snapshot is null)
                        continue;
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
                            StatusChanged?.Invoke(
                                $"RGB: Effect Lab restored baseline · profile {ProfileName(_snapshot.OnboardSlot)}");
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
                        StatusChanged?.Invoke(
                            $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} expired · baseline {ProfileName(_snapshot.OnboardSlot)}");
                    }
                }
                catch (K15HidLightingController.K15ProfileChangedException ex)
                {
                    AdoptActiveProfileLocked(ex.CurrentSlot, startProfileOverlay: true);
                }
                catch (Exception ex) when (IsTransportFault(ex))
                {
                    InvalidateProfileHint();
                    if (!Enabled)
                        continue;
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
        if (_controller is null || !IsCurrentBindingLocked())
            return;

        Thread.Sleep(180);
        var stableSlot = _controller.ReadActiveSlot();
        if (stableSlot != observedSlot)
            observedSlot = stableSlot;

        if (_snapshot?.OnboardSlot == observedSlot)
        {
            PublishActiveSlot(observedSlot, DateTimeOffset.UtcNow);
            return;
        }

        PublishActiveSlot(observedSlot, DateTimeOffset.UtcNow);

        RestorePendingForSlotLocked(_controller, observedSlot, "profile_observed");

        var cachedSnapshot = _snapshots.TryGetValue(observedSlot, out var knownSnapshot);
        var baselineReapplied = false;
        if (cachedSnapshot && knownSnapshot is not null)
        {
            // The profile may still contain a notifier effect from the last time it was active.
            // Reapply its configured baseline policy before any new state effect.
            _controller.Restore(knownSnapshot);
            _snapshot = knownSnapshot;
            baselineReapplied = true;
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
            cachedSnapshot,
            baselineReapplied,
            profileSwitchOverlayEnabled = _config.ProfileSwitch.Enabled
        });

        if (startProfileOverlay && _config.ProfileSwitch.Enabled && _config.ProfileSwitch.DurationSeconds > 0)
        {
            BeginOverlayLocked(_config.ProfileSwitch, $"PROFILE_{ProfileName(observedSlot)}",
                _config.ProfileSwitch.DurationSeconds, "rgb_profile_overlay_started");
            return;
        }

        ApplyDesiredLocked();
    }

    private void RestorePendingForSlotLocked(K15HidLightingController controller, byte slot, string trigger)
    {
        if (!_pendingRestores.TryGetValue(slot, out var pending))
            return;

        // Remove only after Restore has completed successfully. K15HidLightingController.Restore
        // retains the exact active-slot verification for physical race safety.
        DeferredProfileRestore.TryRestore(_pendingRestores, slot, exactSlot: null, controller.Restore);
        Log("rgb_pending_baseline_restored", new
        {
            onboardSlot = slot,
            profile = ProfileName(slot),
            trigger,
            managedNormal = pending.CanonicalNormal.Enabled,
            remainingSlots = _pendingRestores.Keys.OrderBy(value => value).ToArray()
        });
    }

    private void BeginOverlayLocked(LightingEffectConfig source, string kind,
        double durationSeconds, string eventName)
    {
        if (_controller is null || _snapshot is null || !IsCurrentBindingLocked())
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
            palette = StatusLabConfig.PaletteName(source.Palette),
            colors = rendered.Colors,
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
        if (_controller is null || _snapshot is null || !IsCurrentBindingLocked())
            return;

        if (_desiredState == K15NormalizedState.Normal || _expiredState == _desiredState)
        {
            _controller.Restore(_snapshot);
            _appliedState = K15NormalizedState.Normal;
            var policy = _snapshot.CanonicalNormal.Enabled ? "canonical NORMAL" : "exact baseline";
            StatusChanged?.Invoke($"RGB: NORMAL · {policy} {ProfileName(_snapshot.OnboardSlot)}");
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
            palette = StatusLabConfig.PaletteName(source.Palette),
            colors = rendered.Colors,
            brightness = rendered.Brightness,
            speed = rendered.Speed
        });
        StatusChanged?.Invoke(
            $"RGB: {JournalStateNormalizer.ToWireName(_desiredState)} · {rendered.Mode} · profile {ProfileName(_snapshot.OnboardSlot)}");
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

        var reconnectState = _desiredState;
        foreach (var pair in _snapshots)
            _pendingRestores[pair.Key] = pair.Value;
        InvalidateBindingLocked("transport_reconnect");
        try
        {
            if (!_deviceManager.Reconnect() || _deviceManager.Controller is null)
                throw new IOException("Selected K15 device could not be reconnected.");
            _controller = _deviceManager.Controller;
            _bindingGeneration = _deviceManager.ConnectionGeneration;
            if (!IsCurrentBindingLocked())
                throw new IOException("Selected K15 device binding changed during reconnect.");
            var currentSlot = _controller.ReadActiveSlot();
            RestorePendingForSlotLocked(_controller, currentSlot, "transport_reconnect");
            _snapshot = _controller.PrepareProfileSnapshot(_config);
            _snapshots.Clear();
            _snapshots[_snapshot.OnboardSlot] = _snapshot;
            SetDesiredStateLocked(reconnectState);
            ResetVisualStateLocked();
            Enabled = true;
            _transportFailures = 0;

            StatusChanged?.Invoke($"RGB: RECONNECTED · profile {ProfileName(currentSlot)}");
            Log("rgb_transport_reconnected", new { onboardSlot = currentSlot });
            if (IsOverlayActive() && _overlayEffect is not null)
                _controller.ApplyEffect(_snapshot, _overlayEffect, _config.WireColorOrder, _overlayKind);
            else
                ApplyDesiredLocked();
        }
        catch (Exception retryEx)
        {
            _deviceManager.MarkConnectionLost();
            Log("rgb_transport_reconnect_pending", new
            {
                exception = retryEx.GetType().FullName,
                message = retryEx.Message
            });
            StatusChanged?.Invoke($"RGB: RETRYING · {ShortTransportMessage(retryEx)}");
        }
    }

    private void OnDeviceStateChanged(K15DeviceConnectionState state)
    {
        if (state is not K15DeviceConnectionState.Connected)
        {
            InvalidateProfileHint();
            _controller = null;
            _bindingGeneration = null;
            _snapshot = null;
            _snapshots.Clear();
            if (Enabled)
            {
                Enabled = false;
                StatusChanged?.Invoke("RGB: OFF · device unavailable");
                Log("rgb_binding_invalidated", new { reason = "device_state_changed", state });
            }
        }
    }

    private bool IsCurrentBindingLocked() =>
        K15RgbBindingGuard.IsCurrent(
            _controller,
            _bindingGeneration,
            _deviceManager.ConnectionState == K15DeviceConnectionState.Connected,
            _deviceManager.Controller,
            _deviceManager.ConnectionGeneration);

    private void InvalidateBindingLocked(string reason)
    {
        if (!Enabled && _controller is null)
            return;
        _controller = null;
        _bindingGeneration = null;
        _snapshot = null;
        _snapshots.Clear();
        Enabled = false;
        InvalidateProfileHint();
        StatusChanged?.Invoke("RGB: OFF · device unavailable");
        Log("rgb_binding_invalidated", new { reason });
    }

    private void PublishActiveSlot(byte slot, DateTimeOffset observedUtc)
    {
        var hint = _profileHint.Observe(slot, observedUtc);
        ProfileHintChanged?.Invoke(hint);
    }

    private void InvalidateProfileHint()
    {
        var hint = _profileHint.Invalidate(DateTimeOffset.UtcNow);
        ProfileHintChanged?.Invoke(hint);
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
        await _gate.WaitAsync();
        try
        {
            if (!Enabled)
                return;

            // Preserve profile rollback/policy snapshots before attempting any restore. An inactive
            // profile cannot be safely selected by Status Lab, so its repair stays pending until
            // that profile is physically active again. Never forget a deferred repair just because RGB is OFF.
            foreach (var pair in _snapshots)
                _pendingRestores[pair.Key] = pair.Value;

            try
            {
                if (_controller is not null)
                {
                    var currentSlot = _controller.ReadActiveSlot();
                    if (_pendingRestores.TryGetValue(currentSlot, out var currentSnapshot))
                    {
                        _snapshot = currentSnapshot;
                        _controller.Restore(currentSnapshot);
                        _pendingRestores.Remove(currentSlot);
                        Log("rgb_active_profile_restored_on_disable", new
                        {
                            reason,
                            onboardSlot = currentSlot,
                            profile = ProfileName(currentSlot),
                            managedNormal = currentSnapshot.CanonicalNormal.Enabled
                        });
                    }

                    if (_pendingRestores.Count > 0)
                    {
                        Log("rgb_inactive_profile_restore_deferred", new
                        {
                            reason,
                            activeSlot = currentSlot,
                            deferredSlots = _pendingRestores.Keys.OrderBy(slot => slot).ToArray()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("rgb_restore_failed_on_disable", new
                {
                    reason,
                    exception = ex.GetType().FullName,
                    hresult = ex.HResult,
                    message = ex.Message,
                    pendingSlots = _pendingRestores.Keys.OrderBy(slot => slot).ToArray()
                });
                StatusChanged?.Invoke($"RGB: OFF · restore pending ({ShortTransportMessage(ex)})");
            }
            finally
            {
                _controller = null;
                _bindingGeneration = null;
                _snapshot = null;
                _snapshots.Clear();
                _desiredState = K15NormalizedState.Normal;
                _stateVisualUntilUtc = null;
                ResetVisualStateLocked();
                Enabled = false;
                var pending = _pendingRestores.Keys.OrderBy(slot => slot).Select(ProfileName).ToArray();
                StatusChanged?.Invoke(pending.Length == 0
                    ? "RGB: OFF"
                    : $"RGB: OFF · pending baseline {string.Join(",", pending)}");
                Log("rgb_canary_disabled", new
                {
                    reason,
                    programmaticProfileSelection = false,
                    pendingRestoreSlots = _pendingRestores.Keys.OrderBy(slot => slot).ToArray()
                });
            }
        }
        finally
        {
            _gate.Release();
        }

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _deviceManager.StateChanged -= OnDeviceStateChanged;
        _deviceManager.ActiveSlotObserved -= PublishActiveSlot;
        await DisableAsync("application_exit");
        await _monitorLifecycle.DisposeAsync();
        if (_pendingRestores.Count > 0)
        {
            Log("rgb_pending_restore_on_exit", new
            {
                pendingSlots = _pendingRestores.Keys.OrderBy(slot => slot).ToArray(),
                note = "No programmatic profile selection; reopen Status Lab on that physical profile to recover its configured baseline policy."
            });
        }
        _gate.Dispose();
    }
}
