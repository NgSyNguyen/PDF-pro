using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>
/// One page of a page plan: which page of which source file it shows, plus the plan's own edits (extra rotation,
/// crop, annotations added in the session). Immutable; the edit methods return a new instance (or this one when
/// nothing changes).
/// </summary>
/// <remarks>
/// <see cref="PageKey"/> identifies the page across plan edits (moves, undo), so selections, caches and views can
/// follow it. Blank pages have no source file: their <see cref="SourceId"/> is <see cref="SourceIds.Blank"/> and their
/// size is <see cref="BlankSize"/>.
/// </remarks>
public sealed record PageRef
{
    public PageRef(string sourceId, int sourcePageIndex, int rotationDelta, PageCrop? cropBoxOverride, Guid pageKey)
        : this(sourceId, sourcePageIndex, rotationDelta, cropBoxOverride, pageKey, blankSize: null, splitMark: null, annotations: [])
    {
    }

    private PageRef(string sourceId, int sourcePageIndex, int rotationDelta, PageCrop? cropBoxOverride, Guid pageKey, PdfSize? blankSize, SplitMark? splitMark, IReadOnlyList<AnnotationItem> annotations)
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
        SplitMark = splitMark;
        Annotations = annotations;
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

    /// <summary>
    /// Set when the page starts a new file in a split by marks; null otherwise. Not page content: it does not change
    /// how the page looks or what is saved.
    /// </summary>
    public SplitMark? SplitMark { get; }

    /// <summary>
    /// Annotations added to the page in this session, in drawing order, in the PDF user space of the page as written
    /// (the source page's space: rotation and crop do not move them). They are written into the file when the plan
    /// is materialized; annotations already in the source file are not listed here.
    /// </summary>
    public IReadOnlyList<AnnotationItem> Annotations { get; }

    /// <summary><see cref="RotationDelta"/> in clockwise quarter turns (0–3).</summary>
    public int QuarterTurns => RotationDelta / 90;

    public static PageRef Blank(PdfSize size, Guid pageKey) => new(SourceIds.Blank, 0, 0, null, pageKey, size, splitMark: null, annotations: []);

    /// <summary>The same page with a different extra rotation (any multiple of 90 degrees).</summary>
    public PageRef WithRotation(int rotationDelta)
    {
        if (rotationDelta % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDelta), rotationDelta, "Page rotation must be a multiple of 90 degrees.");
        }

        return PageGeometry.NormalizeRotation(rotationDelta) == RotationDelta
            ? this
            : new PageRef(SourceId, SourcePageIndex, rotationDelta, CropBoxOverride, PageKey, BlankSize, SplitMark, Annotations);
    }

    public PageRef WithCrop(PageCrop? crop)
        => crop == CropBoxOverride ? this : new PageRef(SourceId, SourcePageIndex, RotationDelta, crop, PageKey, BlankSize, SplitMark, Annotations);

    /// <summary>A copy of this page under another key (duplicating a page), annotations included; the copy has no split mark.</summary>
    public PageRef WithKey(Guid pageKey)
        => pageKey == PageKey ? this : new PageRef(SourceId, SourcePageIndex, RotationDelta, CropBoxOverride, pageKey, BlankSize, splitMark: null, Annotations);

    /// <summary>The same page with another split mark (null removes it).</summary>
    public PageRef WithSplitMark(SplitMark? mark)
        => mark == SplitMark ? this : new PageRef(SourceId, SourcePageIndex, RotationDelta, CropBoxOverride, PageKey, BlankSize, mark, Annotations);

    /// <summary>The same page with these session annotations (names must be unique on the page).</summary>
    /// <exception cref="ArgumentException">A null annotation, or two with the same name.</exception>
    public PageRef WithAnnotations(IReadOnlyList<AnnotationItem> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (ReferenceEquals(annotations, Annotations) || (annotations.Count == 0 && Annotations.Count == 0))
        {
            return this;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (annotation is null || !names.Add(annotation.Id))
            {
                throw new ArgumentException("Annotations must not be null and their names must be unique on the page.", nameof(annotations));
            }
        }

        return new PageRef(SourceId, SourcePageIndex, RotationDelta, CropBoxOverride, PageKey, BlankSize, SplitMark, [.. annotations]);
    }

    /// <summary>The session annotation named <paramref name="id"/>, or null.</summary>
    public AnnotationItem? FindAnnotation(string id)
    {
        foreach (var annotation in Annotations)
        {
            if (string.Equals(annotation.Id, id, StringComparison.Ordinal))
            {
                return annotation;
            }
        }

        return null;
    }

    /// <summary>True when both show the same page the same way, with the same annotations (split marks aside).</summary>
    public bool HasSameContent(PageRef other)
        => HasSamePage(other) && (ReferenceEquals(Annotations, other.Annotations) || Annotations.SequenceEqual(other.Annotations));

    /// <summary>True when both show the same page the same way, annotations and split marks aside (its text is the same).</summary>
    public bool HasSamePage(PageRef other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return PageKey == other.PageKey && SourcePageIndex == other.SourcePageIndex && RotationDelta == other.RotationDelta
            && CropBoxOverride == other.CropBoxOverride && BlankSize == other.BlankSize
            && string.Equals(SourceId, other.SourceId, StringComparison.Ordinal);
    }

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
