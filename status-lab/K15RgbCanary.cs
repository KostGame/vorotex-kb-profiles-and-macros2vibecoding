namespace Vorotex.K15.StatusLab;

internal sealed class K15RgbCanary : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private K15HidLightingController? _controller;
    private K15HidLightingController.LightingSnapshot? _snapshot;
    private K15NormalizedState _state = K15NormalizedState.Normal;

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
            Enabled = true;
            _state = K15NormalizedState.Normal;

            Log("rgb_canary_enabled", new
            {
                onboardSlot = _snapshot.OnboardSlot,
                originalMode = _snapshot.Header[0]
            });

            StatusChanged?.Invoke($"RGB: CANARY ON · slot {_snapshot.OnboardSlot + 1}");

            if (currentState != K15NormalizedState.Normal)
            {
                _controller.ApplyState(_snapshot, currentState);
                _state = currentState;
                Log("rgb_state_applied", new { state = JournalStateNormalizer.ToWireName(currentState) });
            }
        }
        catch
        {
            _controller?.Dispose();
            _controller = null;
            _snapshot = null;
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
            if (!Enabled || _controller is null || _snapshot is null)
                return;

            if (_state == state)
                return;

            if (state == K15NormalizedState.Normal)
            {
                _controller.Restore(_snapshot);
                Log("rgb_restored", new { reason = "normalized_state_normal" });
            }
            else
            {
                _controller.ApplyState(_snapshot, state);
                Log("rgb_state_applied", new { state = JournalStateNormalizer.ToWireName(state) });
            }

            _state = state;
        }
        catch (Exception ex)
        {
            Log("rgb_canary_error", new
            {
                exception = ex.GetType().FullName,
                hresult = ex.HResult,
                message = ex.Message
            });
            StatusChanged?.Invoke($"RGB: ERROR · {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync(string reason = "manual_disable")
    {
        await _gate.WaitAsync();
        try
        {
            if (!Enabled)
                return;

            try
            {
                if (_controller is not null && _snapshot is not null && _state != K15NormalizedState.Normal)
                {
                    _controller.Restore(_snapshot);
                    Log("rgb_restored", new { reason });
                }
            }
            finally
            {
                _controller?.Dispose();
                _controller = null;
                _snapshot = null;
                _state = K15NormalizedState.Normal;
                Enabled = false;
                StatusChanged?.Invoke("RGB: OFF");
                Log("rgb_canary_disabled", new { reason });
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
        await DisableAsync("application_exit");
        _gate.Dispose();
    }
}
