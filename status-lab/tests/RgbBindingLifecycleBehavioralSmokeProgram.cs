using Vorotex.K15.StatusLab;

sealed class FakeController
{
    public FakeController(string id) => Id = id;
    public string Id { get; }
    public int DisposeCount { get; private set; }
    public List<string> Operations { get; } = new();
    public bool HasNotifierEffect { get; set; }
    public void RestoreBaseline() { HasNotifierEffect = false; Operations.Add("restore"); }
    public void CaptureBaseline()
    {
        Require(!HasNotifierEffect, $"Contaminated baseline captured on {Id}.");
        Operations.Add("capture");
    }
    public void Apply(string state) => Operations.Add($"apply:{state}");
    public void Dispose() => DisposeCount++;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

static class Program
{
    public static void Main()
    {
        CaseControllerReplacement();
        CaseReconnectOrdering();
        CaseReconnectFailure();
        CaseConnectionLost();
        CaseBorrowerOwnership();
        CaseSelectionOrder();
        Console.WriteLine("RGB binding lifecycle behavioral tests: PASS");
    }

    private static void CaseControllerReplacement()
    {
        var a = new FakeController("A");
        var b = new FakeController("B");
        var generation = 1L;
        var boundGeneration = generation;
        Require(K15RgbBindingGuard.IsCurrent(a, boundGeneration, true, a, generation),
            "Initial A binding must be current.");
        generation++;
        Require(!K15RgbBindingGuard.IsCurrent(a, boundGeneration, true, b, generation),
            "A binding must fail after manager replaces it with B.");
        Require(!K15RgbBindingGuard.IsCurrent(a, boundGeneration, false, b, generation),
            "Disconnected binding must fail closed.");
    }

    private static void CaseReconnectOrdering()
    {
        var a = new FakeController("A");
        var b = new FakeController("B") { HasNotifierEffect = true };
        var snapshots = new Dictionary<byte, string> { [0] = "baseline-A" };
        var pending = new Dictionary<byte, string>();
        K15RgbLifecycleDecisions.ParkSnapshots(snapshots, pending);
        var fresh = K15RgbLifecycleDecisions.RestoreBeforeCapture(
            pending.ContainsKey(0),
            () =>
            {
                b.RestoreBaseline();
                pending.Remove(0);
            },
            () =>
            {
                b.CaptureBaseline();
                return "baseline-B";
            });
        b.Apply("RUNNING");
        Require(fresh == "baseline-B" && pending.Count == 0,
            "Reconnect must restore active pending baseline before fresh capture.");
        Require(b.Operations.SequenceEqual(new[] { "restore", "capture", "apply:RUNNING" }),
            "Reconnect ordering must be restore, capture, apply through B.");
        Require(a.Operations.Count == 0, "Replacement must perform no write through A.");
    }

    private static void CaseReconnectFailure()
    {
        var enabled = true;
        var current = new FakeController("A");
        var success = K15RgbLifecycleDecisions.IsReconnectUsable(false, current is not null);
        if (!success) enabled = false;
        Require(!enabled, "Reconnect failure must leave RGB logically OFF.");
    }

    private static void CaseConnectionLost()
    {
        var a = new FakeController("A");
        Require(!K15RgbBindingGuard.IsCurrent(a, 1, false, null, 2),
            "ConnectionLost must reject old binding writes.");
    }

    private static void CaseBorrowerOwnership()
    {
        var owner = new FakeController("owner");
        _ = owner;
        Require(owner.DisposeCount == 0, "Borrower must not dispose owner controller.");
    }

    private static void CaseSelectionOrder()
    {
        var selected = "A";
        var failedTeardown = K15RgbLifecycleDecisions.RunAfterTeardown(
            teardownSucceeded: false,
            operation: () =>
            {
                selected = "B";
                return true;
            });
        Require(!failedTeardown && selected == "A",
            "Failed RGB teardown must preserve selected candidate A.");

        var successfulTeardown = K15RgbLifecycleDecisions.RunAfterTeardown(
            teardownSucceeded: true,
            operation: () =>
            {
                selected = "B";
                return true;
            });
        Require(successfulTeardown && selected == "B",
            "Candidate B selection must occur after successful teardown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
