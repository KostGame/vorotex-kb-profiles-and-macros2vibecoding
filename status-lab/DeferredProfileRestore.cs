namespace Vorotex.K15.StatusLab;

// Small lifecycle seam for deterministic tests. The production controller remains responsible
// for exact active-slot verification; this helper only owns pending-state commit semantics.
internal static class DeferredProfileRestore
{
    public static bool TryRestore<TSnapshot>(
        IDictionary<byte, TSnapshot> pending,
        byte observedSlot,
        Func<TSnapshot, bool>? exactSlot,
        Action<TSnapshot> restore)
    {
        if (!pending.TryGetValue(observedSlot, out var snapshot))
            return false;

        if (exactSlot is not null && !exactSlot(snapshot))
            return false;

        restore(snapshot);
        pending.Remove(observedSlot);
        return true;
    }
}
