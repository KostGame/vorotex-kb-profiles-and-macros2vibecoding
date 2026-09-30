namespace Vorotex.K15.StatusLab;

internal enum K15LayoutAuthorityState
{
    Disconnected,
    SlotOnly,
    SyncingLayout,
    ReadyVerified,
    ReadyActionVerified,
    Stale,
    Unsupported,
    Error
}

internal sealed record K15PreparedDispatch(
    byte ActiveSlot,
    string ControlId,
    K15LocalSemanticAction Action,
    K15DispatchPlan Plan,
    long AuthorityVersion);

internal sealed class K15LayoutAuthoritySession : IDisposable
{
    private readonly IK15LayoutAuthorityDeviceSource _deviceManager;
    private readonly K15LayoutFilePaths _paths;
    private K15HardwareLayoutSnapshot? _hardware;
    private K15LocalSemanticSnapshot? _local;
    private K15LayoutFileStamp? _stamp;
    private long _connectionGeneration;
    private string? _identityFingerprint;
    private readonly List<FileSystemWatcher> _watchers = new();
    private long _fileMutationGeneration;
    private readonly K15LayoutAuthorityVersionGuard _authorityGuard = new();

    internal K15LayoutAuthoritySession(
        IK15LayoutAuthorityDeviceSource deviceManager,
        K15LayoutFilePaths? paths = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _paths = paths ?? K15LayoutFilePaths.ResolveDefault();
        State = K15LayoutAuthorityState.Disconnected;
        _deviceManager.AuthorityChanged += HandleDeviceAuthorityChanged;
        CreateFileWatchers();
    }

    internal K15LayoutAuthorityState State { get; private set; }
    internal bool HasDispatchAuthority => CanDispatchFrom(State);
    internal event Action<K15LayoutAuthorityState>? StateChanged;

    internal K15LayoutAttestationResult Refresh()
    {
        var controller = RequireConnectedController();
        SetState(K15LayoutAuthorityState.SyncingLayout);

        try
        {
            var authorityVersion = _authorityGuard.Capture(version => version);
            var generation = _deviceManager.ConnectionGeneration;
            var fileGeneration = Interlocked.Read(ref _fileMutationGeneration);
            var identity = _deviceManager.IdentityFingerprint
                ?? throw new InvalidOperationException("Connected K15 identity is unavailable.");

            var slot = controller.ReadActiveSlot();
            SetState(K15LayoutAuthorityState.SlotOnly);

            var localBefore = K15LayoutFileAuthority.Read(_paths, slot);
            var hardware = K15LayoutAuthority.Capture(controller);
            var localAfter = K15LayoutFileAuthority.Read(_paths, slot);

            if (generation != _deviceManager.ConnectionGeneration ||
                !string.Equals(identity, _deviceManager.IdentityFingerprint, StringComparison.Ordinal) ||
                hardware.ActiveSlot != slot ||
                localBefore.Stamp != localAfter.Stamp ||
                fileGeneration != Interlocked.Read(ref _fileMutationGeneration))
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                return new K15LayoutAttestationResult(
                    K15LayoutVerificationState.Stale,
                    new[] { "authority changed during synchronization" });
            }

            var result = K15LayoutAttestation.Compare(hardware, localAfter.Semantic);
            if (!result.IsVerified)
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                return result;
            }

            if (!_authorityGuard.TryPublish(authorityVersion, () =>
            {
                _hardware = hardware;
                _local = localAfter.Semantic;
                _stamp = localAfter.Stamp;
                _connectionGeneration = generation;
                _identityFingerprint = identity;
                State = K15LayoutAuthorityState.ReadyVerified;
            }))
            {
                return new K15LayoutAttestationResult(
                    K15LayoutVerificationState.Stale,
                    new[] { "authority changed before synchronization could publish" });
            }
            StateChanged?.Invoke(K15LayoutAuthorityState.ReadyVerified);
            return result;
        }
        catch (InvalidDataException)
        {
            Invalidate(K15LayoutAuthorityState.Unsupported);
            throw;
        }
        catch
        {
            Invalidate(K15LayoutAuthorityState.Error);
            throw;
        }
    }

    internal Task<K15LayoutAttestationResult> RefreshCooperativelyAsync() =>
        K15LayoutAuthorityRecoveryRunner.RunAsync(RefreshCooperativelyOnWorkerAsync);

    private async Task<K15LayoutAttestationResult> RefreshCooperativelyOnWorkerAsync()
    {
        var controller = RequireConnectedController();
        SetState(K15LayoutAuthorityState.SyncingLayout);

        try
        {
            var authorityVersion = _authorityGuard.Capture(version => version);
            var generation = _deviceManager.ConnectionGeneration;
            var fileGeneration = Interlocked.Read(ref _fileMutationGeneration);
            var identity = _deviceManager.IdentityFingerprint
                ?? throw new InvalidOperationException("Connected K15 identity is unavailable.");

            var slot = controller.ReadActiveSlot();
            SetState(K15LayoutAuthorityState.SlotOnly);

            var localBefore = K15LayoutFileAuthority.Read(_paths, slot);
            var hardware = await K15LayoutAuthority.CaptureCooperativelyAsync(controller);
            var localAfter = K15LayoutFileAuthority.Read(_paths, slot);

            if (generation != _deviceManager.ConnectionGeneration ||
                !string.Equals(identity, _deviceManager.IdentityFingerprint, StringComparison.Ordinal) ||
                hardware.ActiveSlot != slot ||
                localBefore.Stamp != localAfter.Stamp ||
                fileGeneration != Interlocked.Read(ref _fileMutationGeneration))
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                return new K15LayoutAttestationResult(
                    K15LayoutVerificationState.Stale,
                    new[] { "authority changed during synchronization" });
            }

            var result = K15LayoutAttestation.Compare(hardware, localAfter.Semantic);
            if (!result.IsVerified)
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                return result;
            }

            if (!_authorityGuard.TryPublish(authorityVersion, () =>
            {
                _hardware = hardware;
                _local = localAfter.Semantic;
                _stamp = localAfter.Stamp;
                _connectionGeneration = generation;
                _identityFingerprint = identity;
                State = K15LayoutAuthorityState.ReadyVerified;
            }))
            {
                return new K15LayoutAttestationResult(
                    K15LayoutVerificationState.Stale,
                    new[] { "authority changed before synchronization could publish" });
            }
            StateChanged?.Invoke(K15LayoutAuthorityState.ReadyVerified);
            return result;
        }
        catch (InvalidDataException)
        {
            Invalidate(K15LayoutAuthorityState.Unsupported);
            throw;
        }
        catch
        {
            Invalidate(K15LayoutAuthorityState.Error);
            throw;
        }
    }

    internal K15PreparedDispatch PrepareDispatch(string controlId)
    {
        K15HardwareLayoutSnapshot hardware;
        K15LocalSemanticSnapshot local;
        K15LayoutFileStamp stamp;
        long authorityVersion;
        long connectionGeneration;
        string? identityFingerprint;
        var snapshot = _authorityGuard.Capture(version =>
        {
            if (!CanDispatchFrom(State) ||
                _hardware is null || _local is null || _stamp is null)
                throw new InvalidOperationException("K15 layout authority is not action-ready.");
            return (hardware: _hardware, local: _local, stamp: _stamp, version,
                connectionGeneration: _connectionGeneration, identityFingerprint: _identityFingerprint);
        });
        hardware = snapshot.hardware;
        local = snapshot.local;
        stamp = snapshot.stamp;
        authorityVersion = snapshot.version;
        connectionGeneration = snapshot.connectionGeneration;
        identityFingerprint = snapshot.identityFingerprint;

        if (!CanDispatchFrom(State))
            throw new InvalidOperationException("K15 layout authority is not action-ready.");

        var controller = RequireConnectedController();
        if (_deviceManager.ConnectionGeneration != connectionGeneration ||
            !string.Equals(_deviceManager.IdentityFingerprint, identityFingerprint,
                StringComparison.Ordinal) ||
            !K15LayoutFileAuthority.Matches(_paths, stamp))
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new InvalidOperationException("K15 layout authority became stale before dispatch.");
        }

        var activeSlot = controller.ReadActiveSlot();
        if (activeSlot != hardware.ActiveSlot || activeSlot != local.Slot)
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new K15LayoutProfileChangedException(hardware.ActiveSlot, activeSlot);
        }

        if (!local.Actions.TryGetValue(controlId, out var action))
            throw new KeyNotFoundException($"Unknown Mini-K15 control '{controlId}'.");

        var cell = K15LayoutAuthorityModel.BindingCellForControl(controlId);
        var liveBinding = controller.ReadBindingCell(cell);
        if (!liveBinding.AsSpan().SequenceEqual(action.ExpectedBindingRaw))
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new InvalidOperationException($"K15 binding changed for '{controlId}'.");
        }

        if (action.MacroMemorySlot is byte macroSlot)
        {
            if (action.MacroPayload is null)
                throw new InvalidDataException($"Local macro payload missing for '{controlId}'.");

            var livePayload = controller.ReadMacroPayload(macroSlot);
            if (!livePayload.AsSpan().SequenceEqual(action.MacroPayload))
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                throw new InvalidOperationException($"K15 macro payload changed for '{controlId}'.");
            }
        }

        var finalSlot = controller.ReadActiveSlot();
        if (finalSlot != activeSlot)
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new K15LayoutProfileChangedException(activeSlot, finalSlot);
        }

        if (!_authorityGuard.ExecuteIfCurrent(authorityVersion, () =>
                CanDispatchFrom(State) &&
                ReferenceEquals(_hardware, hardware) && ReferenceEquals(_local, local) &&
                ReferenceEquals(_stamp, stamp)))
            throw new InvalidOperationException("K15 layout authority changed during dispatch preparation.");

        return new K15PreparedDispatch(activeSlot, controlId, action,
            K15DispatchPlanner.Create(action), authorityVersion);
    }

    internal void AuthorizeDispatch(K15PreparedDispatch prepared)
    {
        if (!_authorityGuard.ExecuteIfCurrent(prepared.AuthorityVersion,
                () => CanDispatchFrom(State) && _hardware is not null &&
                      _local is not null && _stamp is not null &&
                      _connectionGeneration == _deviceManager.ConnectionGeneration &&
                      string.Equals(_identityFingerprint, _deviceManager.IdentityFingerprint,
                          StringComparison.Ordinal) &&
                      _deviceManager.IsConnected &&
                      K15LayoutFileAuthority.Matches(_paths, _stamp)))
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new InvalidOperationException("K15 layout authority changed before dispatch.");
        }
    }

    internal async Task<K15ProfileSwitchResult> SwitchProfileExplicitlyAsync(
        K15PreparedDispatch prepared,
        Func<byte, Task>? onSlotConfirmed = null)
    {
        if (prepared.Plan.Kind != K15DispatchPlanKind.ProfileSwitch ||
            !string.Equals(prepared.ControlId, "rotary", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Prepared dispatch is not a verified rotary profile-switch action.");
        }

        var controller = RequireConnectedController();
        AuthorizeDispatch(prepared);
        var originalStamp = _stamp ?? throw new InvalidOperationException("K15 file authority is unavailable.");
        var targetSlot = checked((byte)(1 - prepared.ActiveSlot));
        var targetLocalBaseline = K15LayoutFileAuthority.Read(_paths, targetSlot);
        var switchGeneration = _deviceManager.ConnectionGeneration;
        var switchIdentity = _deviceManager.IdentityFingerprint;
        var switchFileGeneration = Interlocked.Read(ref _fileMutationGeneration);
        long? targetInvalidationVersion = null;
        var targetInvalidationValid = false;
        var switchDriftDetected = false;
        return await K15ProfileSwitchTransaction.ExecuteAsync(
            controller,
            async expectedSlot =>
            {
                if (expectedSlot == targetSlot)
                    return await RefreshActionBindingsAsync(
                        expectedSlot, targetLocalBaseline.Stamp, targetInvalidationVersion,
                        targetInvalidationValid, switchGeneration, switchIdentity,
                        switchFileGeneration, () => switchDriftDetected = true);

                var rollbackBaselineStable = targetInvalidationValid &&
                    switchGeneration == _deviceManager.ConnectionGeneration &&
                    string.Equals(switchIdentity, _deviceManager.IdentityFingerprint, StringComparison.Ordinal) &&
                    switchFileGeneration == Interlocked.Read(ref _fileMutationGeneration) &&
                    K15LayoutFileAuthority.Matches(_paths, targetLocalBaseline.Stamp);
                if (!rollbackBaselineStable) switchDriftDetected = true;
                var result = await RefreshCooperativelyAsync();
                var rollbackVerified = result.IsVerified && _hardware?.ActiveSlot == expectedSlot &&
                    _local?.Slot == expectedSlot && _stamp == originalStamp;
                if (switchDriftDetected || !rollbackVerified)
                {
                    Invalidate(K15LayoutAuthorityState.Stale);
                    return false;
                }
                return true;
            },
            async confirmedSlot =>
            {
                if (confirmedSlot == targetSlot)
                {
                    targetInvalidationValid = _authorityGuard.TryInvalidate(
                        prepared.AuthorityVersion,
                        () => ClearAuthority(K15LayoutAuthorityState.Stale),
                        out var version);
                    targetInvalidationVersion = targetInvalidationValid ? version : null;
                    if (!targetInvalidationValid) Invalidate(K15LayoutAuthorityState.Stale);
                }
                else
                {
                    Invalidate(K15LayoutAuthorityState.Stale);
                }
                if (onSlotConfirmed is not null)
                    await onSlotConfirmed(confirmedSlot);
            },
            prepared.ActiveSlot);
    }

    private async Task<bool> RefreshActionBindingsAsync(
        byte expectedSlot,
        K15LayoutFileStamp expectedStamp,
        long? expectedAuthorityVersion,
        bool expectedVersionValid,
        long expectedGeneration,
        string? expectedIdentity,
        long expectedFileGeneration,
        Action markSwitchDrift)
    {
        var controller = RequireConnectedController();
        if (!expectedVersionValid || expectedAuthorityVersion is not long authorityVersion)
        {
            markSwitchDrift();
            return false;
        }
        var generation = expectedGeneration;
        var fileGeneration = expectedFileGeneration;
        var identity = _deviceManager.IdentityFingerprint
            ?? throw new InvalidOperationException("Connected K15 identity is unavailable.");

        try
        {
            var localBefore = K15LayoutFileAuthority.Read(_paths, expectedSlot);
            var hardware = K15LayoutAuthority.CaptureBindingsOnly(controller, localBefore.Semantic);
            var localAfter = K15LayoutFileAuthority.Read(_paths, expectedSlot);

            if (!_deviceManager.IsConnected ||
                !ReferenceEquals(controller, _deviceManager.LayoutController) ||
                generation != _deviceManager.ConnectionGeneration ||
                !string.Equals(identity, expectedIdentity, StringComparison.Ordinal) ||
                !string.Equals(identity, _deviceManager.IdentityFingerprint, StringComparison.Ordinal) ||
                hardware.ActiveSlot != expectedSlot || localBefore.Stamp != expectedStamp ||
                localBefore.Stamp != localAfter.Stamp ||
                fileGeneration != Interlocked.Read(ref _fileMutationGeneration))
            {
                markSwitchDrift();
                Invalidate(K15LayoutAuthorityState.Stale);
                return false;
            }

            var result = K15LayoutAttestation.CompareBindingsOnly(hardware, localAfter.Semantic);
            if (!result.IsActionVerified)
            {
                Invalidate(K15LayoutAuthorityState.Stale);
                return false;
            }

            if (!_authorityGuard.TryPublish(authorityVersion, () =>
                {
                    _hardware = hardware;
                    _local = localAfter.Semantic;
                    _stamp = localAfter.Stamp;
                    _connectionGeneration = generation;
                    _identityFingerprint = identity;
                    State = K15LayoutAuthorityState.ReadyActionVerified;
                }))
            {
                markSwitchDrift();
                return false;
            }

            StateChanged?.Invoke(K15LayoutAuthorityState.ReadyActionVerified);
            await Task.CompletedTask;
            return true;
        }
        catch
        {
            markSwitchDrift();
            Invalidate(K15LayoutAuthorityState.Stale);
            throw;
        }
    }

    internal void Invalidate(K15LayoutAuthorityState state = K15LayoutAuthorityState.Stale)
    {
        var changed = false;
        _authorityGuard.Invalidate(() =>
        {
            changed = State != state;
            ClearAuthority(state);
        });
        if (changed) StateChanged?.Invoke(state);
    }

    private IK15LayoutReadControl RequireConnectedController()
    {
        if (!_deviceManager.IsConnected || _deviceManager.LayoutController is not { } controller)
        {
            Invalidate(K15LayoutAuthorityState.Disconnected);
            throw new InvalidOperationException("K15 device is not connected.");
        }
        return controller;
    }

    private void HandleDeviceAuthorityChanged()
    {
        if (!_deviceManager.IsConnected)
            Invalidate(K15LayoutAuthorityState.Disconnected);
    }

    private void ClearAuthority(K15LayoutAuthorityState state)
    {
        _hardware = null;
        _local = null;
        _stamp = null;
        _connectionGeneration = 0;
        _identityFingerprint = null;
        State = state;
    }

    private static bool CanDispatchFrom(K15LayoutAuthorityState state) =>
        state is K15LayoutAuthorityState.ReadyVerified or K15LayoutAuthorityState.ReadyActionVerified;

    private void SetState(K15LayoutAuthorityState state)
    {
        var changed = false;
        _authorityGuard.Execute(() =>
        {
            if (State == state) return;
            State = state;
            changed = true;
        });
        if (!changed) return;
        StateChanged?.Invoke(state);
    }

    private void CreateFileWatchers()
    {
        var targets = new[] { _paths.Profile0, _paths.Profile1, _paths.MacroConfig }
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in targets
                     .Select(Path.GetDirectoryName)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (directory is null || !Directory.Exists(directory)) continue;

            var watcher = new FileSystemWatcher(directory)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.CreationTime
            };
            FileSystemEventHandler changed = (_, e) => HandleFileMutation(e.FullPath, targets);
            RenamedEventHandler renamed = (_, e) =>
            {
                HandleFileMutation(e.OldFullPath, targets);
                HandleFileMutation(e.FullPath, targets);
            };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += (_, _) => HandleAuthorityFileWatcherError();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void HandleFileMutation(string path, IReadOnlySet<string> targets)
    {
        if (!targets.Contains(Path.GetFullPath(path))) return;
        Interlocked.Increment(ref _fileMutationGeneration);
        Invalidate(K15LayoutAuthorityState.Stale);
    }

    private void HandleAuthorityFileWatcherError()
    {
        Interlocked.Increment(ref _fileMutationGeneration);
        Invalidate(K15LayoutAuthorityState.Stale);
    }

    public void Dispose()
    {
        _deviceManager.AuthorityChanged -= HandleDeviceAuthorityChanged;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        Invalidate(K15LayoutAuthorityState.Disconnected);
    }
}
