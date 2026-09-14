using Vorotex.K15.StatusLab;

static class DeferredProfileRestoreBehavioralSmokeProgram
{
    private sealed record Snapshot(byte Slot);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static (Dictionary<byte, Snapshot> Pending, int Writes) NewPending(byte slot)
    {
        return (new Dictionary<byte, Snapshot> { [slot] = new Snapshot(slot) }, 0);
    }

    private static void Main()
    {
        var a = NewPending(1);
        Require(!DeferredProfileRestore.TryRestore(a.Pending, 0, null, _ => a.Writes++), "A must not restore non-pending slot");
        Require(a.Writes == 0 && a.Pending.ContainsKey(1), "A must retain pending B");

        var b = NewPending(1);
        Require(DeferredProfileRestore.TryRestore(b.Pending, 1, null, _ => b.Writes++), "B must restore observed pending slot");
        Require(b.Writes == 1 && b.Pending.Count == 0, "B must remove pending after one restore");

        Require(!DeferredProfileRestore.TryRestore(b.Pending, 1, null, _ => b.Writes++), "C must not restore twice");
        Require(b.Writes == 1, "C must keep write count at one");

        var d = NewPending(1);
        try { DeferredProfileRestore.TryRestore(d.Pending, 1, null, _ => throw new IOException("fake transport")); }
        catch (IOException) { }
        Require(d.Pending.ContainsKey(1) && d.Writes == 0, "D must retain pending after failure");

        var e = NewPending(0);
        Require(DeferredProfileRestore.TryRestore(e.Pending, 0, null, _ => e.Writes++), "E mirror A must restore");
        Require(e.Pending.Count == 0 && e.Writes == 1, "E mirror A must complete once");

        var f = (Pending: new Dictionary<byte, Snapshot>(), Writes: 0);
        Require(!DeferredProfileRestore.TryRestore(f.Pending, 0, null, _ => f.Writes++), "F must not write without pending");
        Require(f.Writes == 0, "F must keep zero writes");

        var g = NewPending(1);
        Require(!DeferredProfileRestore.TryRestore(g.Pending, 1, _ => false, _ => g.Writes++), "G must reject slot race");
        Require(g.Pending.ContainsKey(1) && g.Writes == 0, "G must retain pending after slot validation failure");

        Console.WriteLine("OBSERVE_NON_PENDING_ZERO_WRITES=PASS");
        Console.WriteLine("OBSERVE_PENDING_RESTORE_ONCE=PASS");
        Console.WriteLine("SECOND_OBSERVATION_NO_REWRITE=PASS");
        Console.WriteLine("FAILED_RESTORE_RETAINS_PENDING=PASS");
        Console.WriteLine("PROFILE_RACE_RETAINS_PENDING=PASS");
        Console.WriteLine("MIRROR_A_B_PASS=PASS");
    }
}
