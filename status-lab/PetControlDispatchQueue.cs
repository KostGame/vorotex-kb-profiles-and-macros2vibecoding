namespace Vorotex.K15.StatusLab;

internal enum PetControlSubmission
{
    Started,
    Queued,
    Rejected
}

internal sealed record PetControlDispatchRequest(string ControlId, long ClickObservedTimestamp);

// A single active action and one FIFO pending action. Further input is rejected.
internal sealed class PetControlDispatchQueue
{
    private readonly object _sync = new();
    private bool _active;
    private PetControlDispatchRequest? _pending;

    internal PetControlSubmission Submit(PetControlDispatchRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ControlId);
        lock (_sync)
        {
            if (!_active)
            {
                _active = true;
                return PetControlSubmission.Started;
            }

            if (_pending is null)
            {
                _pending = request;
                return PetControlSubmission.Queued;
            }

            return PetControlSubmission.Rejected;
        }
    }

    // Keeps ownership active when handing off, so a new click cannot overtake.
    internal PetControlDispatchRequest? CompleteAndTakePending()
    {
        lock (_sync)
        {
            var next = _pending;
            _pending = null;
            if (next is null) _active = false;
            return next;
        }
    }
}
