namespace PdfPro.Core.Caching;

/// <summary>
/// Thread-safe least-recently-used cache bounded by a total byte budget (SPEC: render cache, default 512 MB).
/// Inserting beyond the budget evicts the least recently used entries; an entry larger than the whole budget
/// is not cached at all.
/// </summary>
public sealed class ByteBudgetLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new(); // First = most recently used
    private readonly Lock _lock = new();
    private long _budget;
    private long _total;

    public ByteBudgetLruCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
    }

    /// <summary>Raised (outside the lock) for every entry removed to make room or because the budget shrank.</summary>
    public event Action<TKey, TValue>? Evicted;

    public long BudgetBytes
    {
        get
        {
            lock (_lock)
            {
                return _budget;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            List<Entry> evicted;
            lock (_lock)
            {
                _budget = value;
                evicted = TrimToBudget();
            }

            RaiseEvicted(evicted);
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Adds or replaces an entry. Returns false when the entry alone exceeds the budget (not cached).</summary>
    public bool Set(TKey key, TValue value, long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        List<Entry> evicted;
        bool cached;
        lock (_lock)
        {
            RemoveNode(key);
            cached = sizeBytes <= _budget;
            if (cached)
            {
                _map[key] = _order.AddFirst(new Entry(key, value, sizeBytes));
                _total += sizeBytes;
            }

            evicted = TrimToBudget();
        }

        RaiseEvicted(evicted);
        return cached;
    }

    public bool Remove(TKey key)
    {
        lock (_lock)
        {
            return RemoveNode(key);
        }
    }

    /// <summary>Removes every entry whose key matches (e.g. all renders of a closed document). Returns the count.</summary>
    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_lock)
        {
            var doomed = _map.Keys.Where(predicate).ToList();
            foreach (var key in doomed)
            {
                RemoveNode(key);
            }

            return doomed.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
            _total = 0;
        }
    }

    private bool RemoveNode(TKey key)
    {
        if (!_map.Remove(key, out var node))
        {
            return false;
        }

        _order.Remove(node);
        _total -= node.Value.Size;
        return true;
    }

    private List<Entry> TrimToBudget()
    {
        var evicted = new List<Entry>();
        while (_total > _budget && _order.Last is { } last)
        {
            RemoveNode(last.Value.Key);
            evicted.Add(last.Value);
        }

        return evicted;
    }

    private void RaiseEvicted(List<Entry> evicted)
    {
        var handler = Evicted;
        if (handler is null)
        {
            return;
        }

        foreach (var entry in evicted)
        {
            handler(entry.Key, entry.Value);
        }
    }

    private sealed record Entry(TKey Key, TValue Value, long Size);
}
