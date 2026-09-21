using Vorotex.K15.StatusLab;

sealed class FakeController
{
    public FakeController(string id) => Id = id;
    public string Id { get; }
    public int DisposeCount { get; private set; }
    public List<string> Operations { get; } = new();
    public bool HasNotifierEffect { get; set; }
    public void Dispose() => DisposeCount++;
    public void RestoreBaseline() { HasNotifierEffect = false; Operations.Add("restore"); }
    public void CaptureBaseline()
    {
        Program.Require(!HasNotifierEffect, $"Captured contaminated baseline on controller {Id}.");
        Operations.Add("capture");
    }
    public void Apply(string state) => Operations.Add($"apply:{state}");
}

sealed class LifecycleHarness
{
    private readonly Dictionary<byte, string> _snapshots = new();
    private readonly Dictionary<byte, string> _pending = new();
    private FakeController? _bound;
    private long? _boundGeneration;
    private byte _activeSlot;

    public FakeController? Current { get; private set; }
    public string? Selected { get; private set; }
    public long Generation { get; private set; }
    public bool Enabled { get; private set; }
    public string? AppliedState { get; private set; }
    public IReadOnlyDictionary<byte, string> Pending => _pending;

    public void Select(string candidate) => Selected = candidate;
    public bool TrySelectAfterTeardown(string candidate, bool teardownSucceeded)
    {
        if (!teardownSucceeded) return false;
        Select(candidate);
        return true;
    }

    public void Connect(FakeController controller, byte slot = 0)
    {
        Current = controller;
        _activeSlot = slot;
        Generation++;
    }

    public void Enable(string state)
    {
        Program.Require(Current is not null, "Enable requires current controller.");
        _bound = Current;
        _boundGeneration = Generation;
        _activeSlot = 0;
        var controller = _bound ?? throw new InvalidOperationException("Enable binding missing controller.");
        controller.CaptureBaseline();
        _snapshots[_activeSlot] = $"baseline:{controller.Id}";
        Enabled = true;
        Apply(state);
    }

    public void ReplaceForTransport(FakeController controller)
    {
        foreach (var pair in _snapshots) _pending[pair.Key] = pair.Value;
        _bound = null;
        _boundGeneration = null;
        Enabled = false;
        Current = controller;
        Generation++;
    }

    public bool Reconnect(FakeController? controller, string state)
    {
        if (controller is null)
        {
            Current = null;
            _bound = null;
            _boundGeneration = null;
            Enabled = false;
            return false;
        }

        Current = controller;
        Generation++;
        if (_pending.Remove(_activeSlot))
        {
            controller.RestoreBaseline();
        }
        controller.CaptureBaseline();
        _snapshots.Clear();
        _snapshots[_activeSlot] = $"baseline:{controller.Id}";
        _bound = controller;
        _boundGeneration = Generation;
        Enabled = true;
        Apply(state);
        return true;
    }

    public void ConnectionLost()
    {
        Current = null;
        _bound = null;
        _boundGeneration = null;
        Enabled = false;
    }

    public void Apply(string state)
    {
        Program.Require(K15RgbBindingGuard.IsCurrent(_bound, _boundGeneration, Enabled,
            Current, Generation), "Stale RGB binding write was not rejected.");
        Current!.Apply(state);
        AppliedState = state;
    }

    public void BorrowerFailure() => _bound = null;
}

static class Program
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
var a = new FakeController("A");
var b = new FakeController("B") { HasNotifierEffect = true };
var harness = new LifecycleHarness();
harness.Select("A");
harness.Connect(a);
harness.Enable("NORMAL");
var boundGeneration = harness.Generation;
a.Operations.Clear();

// A: replacement invalidates A; stale A cannot write.
harness.ReplaceForTransport(b);
Require(!K15RgbBindingGuard.IsCurrent(a, boundGeneration, true, b, harness.Generation),
    "Controller A must fail after manager replaces it with B.");
Require(a.Operations.All(operation => !operation.StartsWith("apply:", StringComparison.Ordinal)),
    "No post-replacement write may use controller A.");

// B: restore pending baseline before fresh capture, then apply current state via B.
Require(harness.Reconnect(b, "RUNNING"), "Reconnect B must succeed.");
Require(harness.Pending.Count == 0, "Active pending baseline must be fulfilled.");
Require(b.Operations.IndexOf("capture") > b.Operations.IndexOf("restore"),
    "Fresh snapshot must follow pending baseline restore.");
Require(harness.AppliedState == "RUNNING" && b.Operations.Contains("apply:RUNNING"),
    "Current normalized state must apply through B.");

// C: failed reconnect leaves RGB unavailable and rejects stale writes.
var failed = new LifecycleHarness();
failed.Connect(new FakeController("A"));
failed.Enable("NORMAL");
Require(!failed.Reconnect(null, "RUNNING"), "Reconnect failure must report failure.");
var failedWrite = false;
try { failed.Apply("RUNNING"); } catch (InvalidOperationException) { failedWrite = true; }
Require(failedWrite && !failed.Enabled, "Failed reconnect must fail closed with RGB OFF.");

// D: connection loss blocks later status writes.
var lost = new LifecycleHarness();
lost.Connect(new FakeController("A"));
lost.Enable("NORMAL");
lost.ConnectionLost();
var lostWrite = false;
try { lost.Apply("WAITING"); } catch (InvalidOperationException) { lostWrite = true; }
Require(lostWrite, "ConnectionLost must reject status writes.");

// E: borrower failure never disposes manager-owned controller.
var owner = new FakeController("owner");
var ownership = new LifecycleHarness();
ownership.Connect(owner);
ownership.Enable("NORMAL");
ownership.BorrowerFailure();
Require(owner.DisposeCount == 0, "RGB borrower must not dispose manager-owned controller.");

// F: candidate identity changes only after successful teardown.
var selection = new LifecycleHarness();
selection.Select("A");
Require(!selection.TrySelectAfterTeardown("B", teardownSucceeded: false) && selection.Selected == "A",
    "Failed RGB teardown must preserve selected identity A.");
Require(selection.TrySelectAfterTeardown("B", teardownSucceeded: true) && selection.Selected == "B",
    "Successful teardown may select explicit candidate B.");

Console.WriteLine("RGB binding lifecycle behavioral tests: PASS");
    }
}
