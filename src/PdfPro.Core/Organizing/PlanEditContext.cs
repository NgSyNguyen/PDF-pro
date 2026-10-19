using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>
/// What a plan operation may need besides the plan: the page sizes of each source file (to crop by margins or to
/// insert pages "the size of the neighbouring page") and the generator of keys for new pages.
/// </summary>
public sealed class PlanEditContext
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> _sourcePageSizes;

    /// <param name="sourcePageSizes">Displayed size (with /Rotate) of every page of every source, by source id.</param>
    /// <param name="keys">Creates the keys of new pages; record or replay them to make an edit repeatable.</param>
    public PlanEditContext(IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> sourcePageSizes, IPageKeyGenerator keys)
    {
        ArgumentNullException.ThrowIfNull(sourcePageSizes);
        ArgumentNullException.ThrowIfNull(keys);
        _sourcePageSizes = sourcePageSizes;
        Keys = keys;
    }

    public IPageKeyGenerator Keys { get; }

    public bool HasSource(string sourceId) => _sourcePageSizes.ContainsKey(sourceId);

    /// <exception cref="ArgumentException">The source is unknown.</exception>
    public int GetSourcePageCount(string sourceId) => SizesOf(sourceId).Count;

    /// <exception cref="ArgumentException">The source is unknown.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The source has no such page.</exception>
    public PdfSize GetSourcePageSize(string sourceId, int pageIndex)
    {
        var sizes = SizesOf(sourceId);
        if (pageIndex < 0 || pageIndex >= sizes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The source has {sizes.Count} pages.");
        }

        return sizes[pageIndex];
    }

    /// <summary>The page size before the plan's edits (the reference frame of <see cref="PageCrop"/>).</summary>
    public PdfSize GetBaseSize(PageRef page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.BlankSize ?? GetSourcePageSize(page.SourceId, page.SourcePageIndex);
    }

    /// <summary>The page size as displayed: cropped and turned by the plan.</summary>
    public PdfSize GetDisplaySize(PageRef page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.GetDisplaySize(GetBaseSize(page));
    }

    private IReadOnlyList<PdfSize> SizesOf(string sourceId)
        => _sourcePageSizes.TryGetValue(sourceId, out var sizes)
            ? sizes
            : throw new ArgumentException($"Unknown source '{sourceId}'.", nameof(sourceId));
}
