namespace Vorotex.K15.StatusLab;

internal enum K15LayoutAuthorityState
{
    Disconnected,
    SlotOnly,
    SyncingLayout,
    ReadyVerified,
    Stale,
    Unsupported,
    Error
}

internal sealed record K15PreparedDispatch(
    byte ActiveSlot,
    string ControlId,
    K15LocalSemanticAction Action,
    K15DispatchPlan Plan);

internal sealed class K15LayoutAuthoritySession : IDisposable
{
    private readonly K15DeviceManager _deviceManager;
    private readonly K15LayoutFilePaths _paths;
    private K15HardwareLayoutSnapshot? _hardware;
    private K15LocalSemanticSnapshot? _local;
    private K15LayoutFileStamp? _stamp;
    private long _connectionGeneration;
    private string? _identityFingerprint;
    private readonly List<FileSystemWatcher> _watchers = new();
    private long _fileMutationGeneration;

    internal K15LayoutAuthoritySession(
        K15DeviceManager deviceManager,
        K15LayoutFilePaths? paths = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _paths = paths ?? K15LayoutFilePaths.ResolveDefault();
        State = K15LayoutAuthorityState.Disconnected;
        _deviceManager.StateChanged += HandleDeviceStateChanged;
        CreateFileWatchers();
    }

    internal K15LayoutAuthorityState State { get; private set; }
    internal event Action<K15LayoutAuthorityState>? StateChanged;

    internal K15LayoutAttestationResult Refresh()
    {
        var controller = RequireConnectedController();
        SetState(K15LayoutAuthorityState.SyncingLayout);

        try
        {
            var generation = _deviceManager.ConnectionGeneration;
            var fileGeneration = Interlocked.Read(ref _fileMutationGeneration);
            var identity = _deviceManager.SelectedDevice?.IdentityFingerprint
                ?? throw new InvalidOperationException("Connected K15 identity is unavailable.");

            var slot = controller.ReadActiveSlot();
            SetState(K15LayoutAuthorityState.SlotOnly);

            var localBefore = K15LayoutFileAuthority.Read(_paths, slot);
            var hardware = K15LayoutAuthority.Capture(controller);
            var localAfter = K15LayoutFileAuthority.Read(_paths, slot);

            if (generation != _deviceManager.ConnectionGeneration ||
                !string.Equals(identity, _deviceManager.SelectedDevice?.IdentityFingerprint, StringComparison.Ordinal) ||
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

            _hardware = hardware;
            _local = localAfter.Semantic;
            _stamp = localAfter.Stamp;
            _connectionGeneration = generation;
            _identityFingerprint = identity;
            SetState(K15LayoutAuthorityState.ReadyVerified);
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

    internal async Task<K15LayoutAttestationResult> RefreshCooperativelyAsync()
    {
        var controller = RequireConnectedController();
        SetState(K15LayoutAuthorityState.SyncingLayout);

        try
        {
            var generation = _deviceManager.ConnectionGeneration;
            var fileGeneration = Interlocked.Read(ref _fileMutationGeneration);
            var identity = _deviceManager.SelectedDevice?.IdentityFingerprint
                ?? throw new InvalidOperationException("Connected K15 identity is unavailable.");

            var slot = controller.ReadActiveSlot();
            SetState(K15LayoutAuthorityState.SlotOnly);

            var localBefore = K15LayoutFileAuthority.Read(_paths, slot);
            var hardware = await K15LayoutAuthority.CaptureCooperativelyAsync(controller);
            var localAfter = K15LayoutFileAuthority.Read(_paths, slot);

            if (generation != _deviceManager.ConnectionGeneration ||
                !string.Equals(identity, _deviceManager.SelectedDevice?.IdentityFingerprint, StringComparison.Ordinal) ||
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

            _hardware = hardware;
            _local = localAfter.Semantic;
            _stamp = localAfter.Stamp;
            _connectionGeneration = generation;
            _identityFingerprint = identity;
            SetState(K15LayoutAuthorityState.ReadyVerified);
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
        if (State != K15LayoutAuthorityState.ReadyVerified ||
            _hardware is null || _local is null || _stamp is null)
            throw new InvalidOperationException("K15 layout authority is not READY_VERIFIED.");

        var controller = RequireConnectedController();
        if (_deviceManager.ConnectionGeneration != _connectionGeneration ||
            !string.Equals(_deviceManager.SelectedDevice?.IdentityFingerprint, _identityFingerprint,
                StringComparison.Ordinal) ||
            !K15LayoutFileAuthority.Matches(_paths, _stamp))
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new InvalidOperationException("K15 layout authority became stale before dispatch.");
        }

        var activeSlot = controller.ReadActiveSlot();
        if (activeSlot != _hardware.ActiveSlot || activeSlot != _local.Slot)
        {
            Invalidate(K15LayoutAuthorityState.Stale);
            throw new K15HidLightingController.K15ProfileChangedException(_hardware.ActiveSlot, activeSlot);
        }

        if (!_local.Actions.TryGetValue(controlId, out var action))
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
            throw new K15HidLightingController.K15ProfileChangedException(activeSlot, finalSlot);
        }

        return new K15PreparedDispatch(activeSlot, controlId, action, K15DispatchPlanner.Create(action));
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
        return await K15ProfileSwitchTransaction.ExecuteAsync(
            controller,
            async expectedSlot =>
            {
                var result = await RefreshCooperativelyAsync();
                return result.IsVerified &&
                       _hardware?.ActiveSlot == expectedSlot &&
                       _local?.Slot == expectedSlot;
            },
            async confirmedSlot =>
            {
                // Exact slot readback proves the presentation may move to the
                // new profile, but the old layout authority is now invalid.
                // Mark it stale BEFORE yielding back to the UI.
                Invalidate(K15LayoutAuthorityState.Stale);
                if (onSlotConfirmed is not null)
                    await onSlotConfirmed(confirmedSlot);
            },
            prepared.ActiveSlot);
    }

    internal void Invalidate(K15LayoutAuthorityState state = K15LayoutAuthorityState.Stale)
    {
        _hardware = null;
        _local = null;
        _stamp = null;
        _connectionGeneration = 0;
        _identityFingerprint = null;
        SetState(state);
    }

    private K15HidLightingController RequireConnectedController()
    {
        if (_deviceManager.ConnectionState != K15DeviceConnectionState.Connected ||
            _deviceManager.Controller is not K15HidLightingController controller)
        {
            Invalidate(K15LayoutAuthorityState.Disconnected);
            throw new InvalidOperationException("K15 device is not connected.");
        }
        return controller;
    }

    private void HandleDeviceStateChanged(K15DeviceConnectionState state)
    {
        if (state != K15DeviceConnectionState.Connected)
            Invalidate(K15LayoutAuthorityState.Disconnected);
    }

    private void SetState(K15LayoutAuthorityState state)
    {
        if (State == state) return;
        State = state;
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
        _deviceManager.StateChanged -= HandleDeviceStateChanged;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        Invalidate(K15LayoutAuthorityState.Disconnected);
    }
}
