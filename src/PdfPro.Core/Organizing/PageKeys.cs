namespace PdfPro.Core.Organizing;

/// <summary>Creates the <see cref="PageRef.PageKey"/> of new pages (duplicates, inserted and blank pages).</summary>
public interface IPageKeyGenerator
{
    Guid NewKey();
}

/// <summary>Fresh random keys.</summary>
public sealed class RandomPageKeys : IPageKeyGenerator
{
    private RandomPageKeys()
    {
    }

    public static RandomPageKeys Instance { get; } = new();

    public Guid NewKey() => Guid.NewGuid();
}

/// <summary>Remembers every key it hands out, so an edit can be saved and later replayed with the same keys.</summary>
public sealed class RecordingPageKeys(IPageKeyGenerator inner) : IPageKeyGenerator
{
    private readonly List<Guid> _keys = [];

    public IReadOnlyList<Guid> Keys => _keys;

    public Guid NewKey()
    {
        var key = inner.NewKey();
        _keys.Add(key);
        return key;
    }
}

/// <summary>Hands out previously recorded keys in order, so replaying an edit rebuilds exactly the same plan.</summary>
public sealed class ReplayPageKeys : IPageKeyGenerator
{
    private readonly IReadOnlyList<Guid> _keys;
    private int _next;

    public ReplayPageKeys(IReadOnlyList<Guid> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <summary>True once every recorded key has been used (a replay that leaves keys over did not match the record).</summary>
    public bool IsExhausted => _next == _keys.Count;

    public Guid NewKey()
        => _next < _keys.Count
            ? _keys[_next++]
            : throw new InvalidOperationException("The replayed edit creates more pages than were recorded.");
}
