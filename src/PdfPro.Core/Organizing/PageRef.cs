using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>
/// One page of a page plan: which page of which source file it shows, plus the plan's own edits (extra rotation,
/// crop). Immutable; the edit methods return a new instance (or this one when nothing changes).
/// </summary>
/// <remarks>
/// <see cref="PageKey"/> identifies the page across plan edits (moves, undo), so selections, caches and views can
/// follow it. Blank pages have no source file: their <see cref="SourceId"/> is <see cref="SourceIds.Blank"/> and their
/// size is <see cref="BlankSize"/>.
/// </remarks>
public sealed record PageRef
{
    public PageRef(string sourceId, int sourcePageIndex, int rotationDelta, PageCrop? cropBoxOverride, Guid pageKey)
        : this(sourceId, sourcePageIndex, rotationDelta, cropBoxOverride, pageKey, blankSize: null)
    {
    }

    private PageRef(string sourceId, int sourcePageIndex, int rotationDelta, PageCrop? cropBoxOverride, Guid pageKey, PdfSize? blankSize)
    {
        if (!SourceIds.IsValid(sourceId))
        {
            throw new ArgumentException("A source id is short lowercase ASCII, e.g. \"s0\".", nameof(sourceId));
        }

        var isBlank = sourceId == SourceIds.Blank;
        if (isBlank != blankSize.HasValue)
        {
            throw new ArgumentException("Only blank pages have a blank size; create them with PageRef.Blank.", nameof(sourceId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sourcePageIndex);
        if (rotationDelta % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDelta), rotationDelta, "Page rotation must be a multiple of 90 degrees.");
        }

        if (pageKey == Guid.Empty)
        {
            throw new ArgumentException("A page key must not be empty.", nameof(pageKey));
        }

        if (blankSize is { } size && !(double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(blankSize), size, "A blank page needs a positive size.");
        }

        SourceId = sourceId;
        SourcePageIndex = isBlank ? 0 : sourcePageIndex;
        RotationDelta = PageGeometry.NormalizeRotation(rotationDelta);
        CropBoxOverride = cropBoxOverride;
        PageKey = pageKey;
        BlankSize = blankSize;
    }

    public string SourceId { get; }

    /// <summary>0-based page index in the source file (0 for blank pages).</summary>
    public int SourcePageIndex { get; }

    /// <summary>Clockwise rotation added by the plan on top of the page's own /Rotate: 0, 90, 180 or 270.</summary>
    public int RotationDelta { get; }

    /// <summary>The area kept by a crop, or null for the whole page.</summary>
    public PageCrop? CropBoxOverride { get; }

    public Guid PageKey { get; }

    /// <summary>Size of a blank page in points (before <see cref="RotationDelta"/>); null for pages of a source file.</summary>
    public PdfSize? BlankSize { get; }

    public bool IsBlank => BlankSize.HasValue;

    /// <summary><see cref="RotationDelta"/> in clockwise quarter turns (0–3).</summary>
    public int QuarterTurns => RotationDelta / 90;

    public static PageRef Blank(PdfSize size, Guid pageKey) => new(SourceIds.Blank, 0, 0, null, pageKey, size);

    /// <summary>The same page with a different extra rotation (any multiple of 90 degrees).</summary>
    public PageRef WithRotation(int rotationDelta)
    {
        if (rotationDelta % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDelta), rotationDelta, "Page rotation must be a multiple of 90 degrees.");
        }

        return PageGeometry.NormalizeRotation(rotationDelta) == RotationDelta
            ? this
            : new PageRef(SourceId, SourcePageIndex, rotationDelta, CropBoxOverride, PageKey, BlankSize);
    }

    public PageRef WithCrop(PageCrop? crop)
        => crop == CropBoxOverride ? this : new PageRef(SourceId, SourcePageIndex, RotationDelta, crop, PageKey, BlankSize);

    /// <summary>A copy of this page under another key (duplicating a page).</summary>
    public PageRef WithKey(Guid pageKey)
        => pageKey == PageKey ? this : new PageRef(SourceId, SourcePageIndex, RotationDelta, CropBoxOverride, pageKey, BlankSize);

    /// <summary>The page size before the plan's edits: the blank size, or the source page as displayed.</summary>
    /// <param name="sourcePageSize">The source page's displayed size (with its own /Rotate); unused for blank pages.</param>
    public PdfSize GetBaseSize(PdfSize sourcePageSize) => BlankSize ?? sourcePageSize;

    /// <summary>The page size as displayed: base size, cropped, then turned by <see cref="RotationDelta"/>.</summary>
    /// <param name="sourcePageSize">The source page's displayed size (with its own /Rotate); unused for blank pages.</param>
    public PdfSize GetDisplaySize(PdfSize sourcePageSize)
    {
        var size = CropBoxOverride?.Size ?? GetBaseSize(sourcePageSize);
        return RotationDelta is 90 or 270 ? new PdfSize(size.Height, size.Width) : size;
    }
}
