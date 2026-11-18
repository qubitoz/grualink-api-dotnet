namespace Grualink;

/// <summary>Remembers which deliveries were already processed.</summary>
public interface IDeliveryDeduplicator
{
    /// <summary>Returns <c>true</c> the first time an id is seen and <c>false</c> for a duplicate.</summary>
    ValueTask<bool> ClaimAsync(string deliveryId, CancellationToken cancellationToken = default);

    /// <summary>Forgets an id so a later retry is processed again (call it when processing failed).</summary>
    ValueTask ReleaseAsync(string deliveryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory deduplication for a single process. Retries of one delivery span up to 24 hours, so ids are
/// kept for 48 hours by default. With several instances, use a shared store (a unique key in your database
/// is enough).
/// </summary>
public sealed class InMemoryDeliveryDeduplicator : IDeliveryDeduplicator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byId = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _oldestFirst = new();
    private readonly TimeSpan _timeToLive;
    private readonly int _maxEntries;
    private readonly Func<DateTimeOffset> _now;

    public InMemoryDeliveryDeduplicator(TimeSpan? timeToLive = null, int maxEntries = 50_000, Func<DateTimeOffset>? now = null)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromHours(48);
        _maxEntries = maxEntries;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Returns <c>true</c> the first time an id is seen and <c>false</c> for a duplicate.</summary>
    public bool Claim(string deliveryId)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);

        lock (_gate)
        {
            var now = _now();

            // Drop what expired and, when full, the oldest entries.
            while (_oldestFirst.First is { } oldest && (oldest.Value.ExpiresAt <= now || _byId.Count >= _maxEntries))
            {
                Remove(oldest);
            }

            if (_byId.TryGetValue(deliveryId, out var existing))
            {
                if (existing.Value.ExpiresAt > now)
                {
                    return false;
                }

                Remove(existing);
            }

            _byId[deliveryId] = _oldestFirst.AddLast(new Entry(deliveryId, now + _timeToLive));
            return true;
        }
    }

    /// <summary>Forgets an id so a later retry is processed again.</summary>
    public void Release(string deliveryId)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);

        lock (_gate)
        {
            if (_byId.TryGetValue(deliveryId, out var existing))
            {
                Remove(existing);
            }
        }
    }

    public ValueTask<bool> ClaimAsync(string deliveryId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Claim(deliveryId));

    public ValueTask ReleaseAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        Release(deliveryId);
        return ValueTask.CompletedTask;
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _byId.Remove(node.Value.Id);
        _oldestFirst.Remove(node);
    }

    private readonly record struct Entry(string Id, DateTimeOffset ExpiresAt);
}
