namespace Vorotex.K15.StatusLab;

internal sealed class NotificationLearningBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<WindowsNotificationObservation> _items = new();

    public NotificationLearningBuffer(int capacity = 50)
    {
        if (capacity is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _items.Count;

    public void Observe(WindowsNotificationObservation observation)
    {
        var existing = _items.First;
        while (existing is not null)
        {
            var next = existing.Next;
            if (string.Equals(existing.Value.Key, observation.Key, StringComparison.Ordinal))
                _items.Remove(existing);
            existing = next;
        }

        _items.AddFirst(observation);
        while (_items.Count > _capacity)
            _items.RemoveLast();
    }

    public WindowsNotificationObservation[] Snapshot() => _items.ToArray();

    public void Clear() => _items.Clear();
}
