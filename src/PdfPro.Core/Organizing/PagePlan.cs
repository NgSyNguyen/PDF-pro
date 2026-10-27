using System.Collections;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>
/// The virtual document (SPEC "PagePlan — tài liệu ảo"): the ordered pages of a document as references into source
/// files. Immutable and cheap to edit: every edit returns a new plan that shares the unchanged <see cref="PageRef"/>
/// instances, so plans can be kept for undo and handed to background threads without copying.
/// </summary>
/// <remarks>
/// Page positions passed to the edit methods are 0-based indices into this plan. Index lists are treated as sets
/// (order and duplicates do not matter) and must not be empty; out-of-range indices throw. An edit that changes
/// nothing returns this same instance, so callers can skip recording a no-op.
/// </remarks>
public sealed class PagePlan : IReadOnlyList<PageRef>
{
    private readonly PageRef[] _pages;
    private Dictionary<Guid, int>? _indexByKey;

    private PagePlan(PageRef[] pages) => _pages = pages;

    public int Count => _pages.Length;

    public PageRef this[int index] => _pages[index];

    /// <summary>A plan from explicit pages (restoring a saved plan). Page keys must be unique.</summary>
    public static PagePlan Create(IEnumerable<PageRef> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var array = pages.ToArray();
        var keys = new HashSet<Guid>(array.Length);
        foreach (var page in array)
        {
            if (page is null)
            {
                throw new ArgumentException("A plan must not contain null pages.", nameof(pages));
            }

            if (!keys.Add(page.PageKey))
            {
                throw new ArgumentException("Page keys must be unique within a plan.", nameof(pages));
            }
        }

        return new PagePlan(array);
    }

    /// <summary>Every page of a source in its original order, without edits (a freshly opened document).</summary>
    public static PagePlan Identity(string sourceId, int pageCount, IPageKeyGenerator keys)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentNullException.ThrowIfNull(keys);
        var pages = new PageRef[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            pages[i] = new PageRef(sourceId, i, 0, null, keys.NewKey());
        }

        return Create(pages);
    }

    public IEnumerator<PageRef> GetEnumerator() => ((IEnumerable<PageRef>)_pages).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Position of the page with <paramref name="pageKey"/>, or -1.</summary>
    public int IndexOf(Guid pageKey)
    {
        var map = Volatile.Read(ref _indexByKey);
        if (map is null)
        {
            // Plans are shared across threads: build the index once, first writer wins.
            map = new Dictionary<Guid, int>(_pages.Length);
            for (var i = 0; i < _pages.Length; i++)
            {
                map[_pages[i].PageKey] = i;
            }

            map = Interlocked.CompareExchange(ref _indexByKey, map, null) ?? map;
        }

        return map.TryGetValue(pageKey, out var index) ? index : -1;
    }

    /// <summary>Position of the first page showing page <paramref name="sourcePageIndex"/> of a source, or -1.</summary>
    public int FirstIndexOf(string sourceId, int sourcePageIndex)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var page = _pages[i];
            if (page.SourcePageIndex == sourcePageIndex && string.Equals(page.SourceId, sourceId, StringComparison.Ordinal) && !page.IsBlank)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True when the plan shows every page of the source once, in order, without rotation, crop or annotations.</summary>
    public bool IsIdentity(string sourceId, int pageCount)
    {
        if (_pages.Length != pageCount)
        {
            return false;
        }

        for (var i = 0; i < _pages.Length; i++)
        {
            var page = _pages[i];
            if (page.IsBlank || page.SourcePageIndex != i || page.RotationDelta != 0 || page.CropBoxOverride is not null || page.Annotations.Count > 0
                || !string.Equals(page.SourceId, sourceId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The source files the plan draws pages from (blank pages excluded).</summary>
    public IReadOnlySet<string> GetSourceIds()
        => _pages.Where(p => !p.IsBlank).Select(p => p.SourceId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Moves pages, keeping their relative order, so that they land before the page currently at
    /// <paramref name="targetIndex"/> (<see cref="Count"/> moves them to the end).
    /// </summary>
    public PagePlan Move(IReadOnlyCollection<int> indices, int targetIndex)
    {
        var selected = Selection(indices);
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetIndex, Count);

        var isMoved = new bool[Count];
        foreach (var index in selected)
        {
            isMoved[index] = true;
        }

        var result = new List<PageRef>(Count);
        var insertAt = 0;
        for (var i = 0; i < Count; i++)
        {
            if (i == targetIndex)
            {
                insertAt = result.Count;
            }

            if (!isMoved[i])
            {
                result.Add(_pages[i]);
            }
        }

        if (targetIndex == Count)
        {
            insertAt = result.Count;
        }

        result.InsertRange(insertAt, selected.Select(i => _pages[i]));
        return SameOrder(result) ? this : new PagePlan([.. result]);
    }

    /// <summary>Turns pages clockwise by <paramref name="delta"/> degrees (any multiple of 90; negative turns left).</summary>
    public PagePlan Rotate(IReadOnlyCollection<int> indices, int delta)
    {
        var selected = Selection(indices);
        if (delta % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "Page rotation must be a multiple of 90 degrees.");
        }

        if (PageGeometry.NormalizeRotation(delta) == 0)
        {
            return this;
        }

        var pages = (PageRef[])_pages.Clone();
        foreach (var index in selected)
        {
            pages[index] = pages[index].WithRotation(pages[index].RotationDelta + delta);
        }

        return new PagePlan(pages);
    }

    /// <summary>Removes pages. A document keeps at least one page.</summary>
    /// <exception cref="ArgumentException">Every page would be removed.</exception>
    public PagePlan Delete(IReadOnlyCollection<int> indices)
    {
        var selected = Selection(indices);
        if (selected.Length == Count)
        {
            throw new ArgumentException("A document must keep at least one page.", nameof(indices));
        }

        var removed = new bool[Count];
        foreach (var index in selected)
        {
            removed[index] = true;
        }

        return new PagePlan(_pages.Where((_, i) => !removed[i]).ToArray());
    }

    /// <summary>
    /// Copies pages (with their rotation, crop and annotations, without split marks) under new keys. The copies are inserted as one
    /// block, in page order, right after the last selected page.
    /// </summary>
    public PagePlan Duplicate(IReadOnlyCollection<int> indices, IPageKeyGenerator keys)
    {
        var selected = Selection(indices);
        ArgumentNullException.ThrowIfNull(keys);
        var newKeys = NewUniqueKeys(keys, selected.Length);
        var copies = selected.Select((index, i) => _pages[index].WithKey(newKeys[i])).ToArray();
        return InsertAt(selected[^1] + 1, copies);
    }

    /// <summary>Inserts <paramref name="count"/> blank pages of <paramref name="size"/> before position <paramref name="index"/>.</summary>
    public PagePlan InsertBlank(int index, PdfSize size, IPageKeyGenerator keys, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var newKeys = NewUniqueKeys(keys, count);
        return InsertAt(index, newKeys.Select(key => PageRef.Blank(size, key)).ToArray());
    }

    /// <summary>
    /// Inserts pages of another source before position <paramref name="index"/>, in the given order (a page may appear
    /// more than once). Checking the page numbers against the source is the caller's job.
    /// </summary>
    public PagePlan InsertFromSource(int index, string sourceId, IReadOnlyList<int> sourcePages, IPageKeyGenerator keys)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        ArgumentNullException.ThrowIfNull(sourcePages);
        ArgumentNullException.ThrowIfNull(keys);
        if (sourceId == SourceIds.Blank)
        {
            throw new ArgumentException("Blank pages are inserted with InsertBlank.", nameof(sourceId));
        }

        if (sourcePages.Count == 0)
        {
            throw new ArgumentException("At least one page must be inserted.", nameof(sourcePages));
        }

        var newKeys = NewUniqueKeys(keys, sourcePages.Count);
        var pages = sourcePages.Select((page, i) => new PageRef(sourceId, page, 0, null, newKeys[i])).ToArray();
        return InsertAt(index, pages);
    }

    /// <summary>Gives pages the same crop, or removes their crop when <paramref name="crop"/> is null.</summary>
    public PagePlan Crop(IReadOnlyCollection<int> indices, PageCrop? crop)
    {
        var selected = Selection(indices);
        return Crop(selected.ToDictionary(i => i, _ => crop));
    }

    /// <summary>Sets each listed page's crop (null removes it); pages of different sizes get different rectangles.</summary>
    public PagePlan Crop(IReadOnlyDictionary<int, PageCrop?> crops)
    {
        ArgumentNullException.ThrowIfNull(crops);
        _ = Selection(crops.Keys.ToArray(), nameof(crops));
        PageRef[]? pages = null;
        foreach (var (index, crop) in crops)
        {
            var cropped = _pages[index].WithCrop(crop);
            if (!ReferenceEquals(cropped, _pages[index]))
            {
                pages ??= (PageRef[])_pages.Clone();
                pages[index] = cropped;
            }
        }

        return pages is null ? this : new PagePlan(pages);
    }

    /// <summary>True when at least one page carries annotations added in the session.</summary>
    public bool HasAnnotations => Array.Exists(_pages, p => p.Annotations.Count > 0);

    /// <summary>Gives the page at <paramref name="index"/> these session annotations.</summary>
    /// <exception cref="ArgumentException">A null annotation, or two with the same name.</exception>
    public PagePlan WithAnnotations(int index, IReadOnlyList<AnnotationItem> annotations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        var page = _pages[index].WithAnnotations(annotations);
        if (ReferenceEquals(page, _pages[index]))
        {
            return this;
        }

        var pages = (PageRef[])_pages.Clone();
        pages[index] = page;
        return new PagePlan(pages);
    }

    /// <summary>True when at least one page carries a split mark.</summary>
    public bool HasSplitMarks => Array.Exists(_pages, p => p.SplitMark is not null);

    /// <summary>
    /// Positions of the pages that start a file in a split by marks, ascending: always 0 (the first page starts the
    /// first file), then every marked page.
    /// </summary>
    public IReadOnlyList<int> GetSplitStarts()
    {
        var starts = new List<int> { 0 };
        for (var i = 1; i < _pages.Length; i++)
        {
            if (_pages[i].SplitMark is not null)
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// Marks pages as first pages of split files (a page already marked keeps its file name), or removes their marks.
    /// </summary>
    public PagePlan MarkSplit(IReadOnlyCollection<int> indices, bool marked)
    {
        var selected = Selection(indices);
        return ReplaceMarks(selected, (_, page) => marked ? page.SplitMark ?? SplitMark.Unnamed : null);
    }

    /// <summary>Replaces every split mark: exactly the pages listed in <paramref name="marks"/> are marked.</summary>
    public PagePlan WithSplitMarks(IReadOnlyDictionary<int, SplitMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (marks.Count > 0)
        {
            _ = Selection(marks.Keys.ToArray(), nameof(marks));
        }

        return ReplaceMarks(Enumerable.Range(0, Count), (index, _) => marks.GetValueOrDefault(index));
    }

    /// <summary>
    /// This plan with the split marks of <paramref name="previous"/>, page for page: a content change (new revision)
    /// that kept the page count keeps the marks. This same instance when the page counts differ.
    /// </summary>
    public PagePlan WithSplitMarksOf(PagePlan previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return previous.Count != Count ? this : ReplaceMarks(Enumerable.Range(0, Count), (index, _) => previous[index].SplitMark);
    }

    /// <summary>True when both plans show the same pages, in the same order and the same way (split marks aside).</summary>
    public bool HasSameContent(PagePlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this))
        {
            return true;
        }

        if (other.Count != Count)
        {
            return false;
        }

        for (var i = 0; i < _pages.Length; i++)
        {
            if (!_pages[i].HasSameContent(other._pages[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when both plans show the same pages in the same order and the same way, annotations and split marks aside:
    /// what depends on the pages and their text (selections, search results) still holds.
    /// </summary>
    public bool HasSamePages(PagePlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this))
        {
            return true;
        }

        if (other.Count != Count)
        {
            return false;
        }

        for (var i = 0; i < _pages.Length; i++)
        {
            if (!_pages[i].HasSamePage(other._pages[i]))
            {
                return false;
            }
        }

        return true;
    }

    private PagePlan ReplaceMarks(IEnumerable<int> indices, Func<int, PageRef, SplitMark?> markOf)
    {
        PageRef[]? pages = null;
        foreach (var index in indices)
        {
            var marked = _pages[index].WithSplitMark(markOf(index, _pages[index]));
            if (!ReferenceEquals(marked, _pages[index]))
            {
                pages ??= (PageRef[])_pages.Clone();
                pages[index] = marked;
            }
        }

        return pages is null ? this : new PagePlan(pages);
    }

    /// <summary>Validated, sorted, distinct page positions.</summary>
    private int[] Selection(IReadOnlyCollection<int> indices, string parameterName = "indices")
    {
        ArgumentNullException.ThrowIfNull(indices, parameterName);
        if (indices.Count == 0)
        {
            throw new ArgumentException("At least one page must be selected.", parameterName);
        }

        foreach (var index in indices)
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(parameterName, index, $"Page index must be between 0 and {Count - 1}.");
            }
        }

        return indices.Distinct().Order().ToArray();
    }

    private PagePlan InsertAt(int index, PageRef[] inserted)
    {
        var pages = new PageRef[Count + inserted.Length];
        Array.Copy(_pages, 0, pages, 0, index);
        Array.Copy(inserted, 0, pages, index, inserted.Length);
        Array.Copy(_pages, index, pages, index + inserted.Length, Count - index);
        return new PagePlan(pages);
    }

    private Guid[] NewUniqueKeys(IPageKeyGenerator keys, int count)
    {
        var result = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            var key = keys.NewKey();
            if (key == Guid.Empty || IndexOf(key) >= 0 || Array.IndexOf(result, key, 0, i) >= 0)
            {
                throw new InvalidOperationException("The key generator returned a key that is empty or already in use.");
            }

            result[i] = key;
        }

        return result;
    }

    private bool SameOrder(List<PageRef> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (!ReferenceEquals(pages[i], _pages[i]))
            {
                return false;
            }
        }

        return true;
    }
}
