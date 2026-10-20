using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>
/// One page of the virtual document: a page of a source PDF shown with the plan's extra rotation and crop, or a blank
/// page (no source). Knows how to ask PDFium for exactly what the viewer displays.
/// </summary>
public sealed class VirtualPage
{
    internal VirtualPage(PageRef pageRef, IPdfDocument? source, PdfSize baseSize)
    {
        Ref = pageRef;
        Source = source;
        BaseSize = baseSize;
        DisplaySize = pageRef.GetDisplaySize(baseSize);
    }

    public PageRef Ref { get; }

    public Guid Key => Ref.PageKey;

    /// <summary>The opened source document; null for blank pages.</summary>
    public IPdfDocument? Source { get; }

    public bool IsBlank => Source is null;

    public int SourcePageIndex => Ref.SourcePageIndex;

    /// <summary>The plan's extra clockwise rotation in quarter turns.</summary>
    public int QuarterTurns => Ref.QuarterTurns;

    public PageCrop? Crop => Ref.CropBoxOverride;

    /// <summary>The source page as displayed with its own /Rotate (or the blank size): the crop's reference frame.</summary>
    public PdfSize BaseSize { get; }

    /// <summary>The page as displayed: cropped, then turned by the plan.</summary>
    public PdfSize DisplaySize { get; }

    /// <summary>
    /// PDFium options that render this page (or the <paramref name="region"/> of it, in pixels of the displayed page)
    /// from its source page.
    /// </summary>
    /// <param name="scale">Device pixels per point.</param>
    /// <param name="region">Part of the displayed page, or null for all of it.</param>
    /// <param name="flags">Annotations, printing…</param>
    /// <param name="extraQuarterTurns">More clockwise turns on top of the plan's (printing on rotated paper; -turns for upright thumbnails).</param>
    public PageRenderOptions RenderOptions(double scale, PixelRect? region, PdfRenderFlags flags, int extraQuarterTurns = 0)
    {
        var turns = ((QuarterTurns + extraQuarterTurns) % 4 + 4) % 4;
        if (Crop is not { } crop)
        {
            return new PageRenderOptions { Scale = scale, Region = region, Flags = flags, ExtraQuarterTurns = turns };
        }

        var area = CropPixels(crop, scale, turns);
        var part = region is { } r ? new PixelRect(area.X + r.X, area.Y + r.Y, r.Width, r.Height) : area;
        return new PageRenderOptions { Scale = scale, Region = part, Flags = flags, ExtraQuarterTurns = turns };
    }

    /// <summary>The kept area in pixels of the source page rendered with <paramref name="turns"/> extra turns.</summary>
    private PixelRect CropPixels(PageCrop crop, double scale, int turns)
    {
        var turned = crop.Rotate(BaseSize, turns);
        var fullWidth = PixelSize.FromPoints(turns % 2 == 1 ? BaseSize.Height : BaseSize.Width, scale);
        var fullHeight = PixelSize.FromPoints(turns % 2 == 1 ? BaseSize.Width : BaseSize.Height, scale);
        var width = Math.Min(PixelSize.FromPoints(turned.Width, scale), fullWidth);
        var height = Math.Min(PixelSize.FromPoints(turned.Height, scale), fullHeight);
        var x = Math.Clamp((int)Math.Round(turned.Left * scale, MidpointRounding.AwayFromZero), 0, fullWidth - width);
        var y = Math.Clamp((int)Math.Round(turned.Top * scale, MidpointRounding.AwayFromZero), 0, fullHeight - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// A point of the source page (points, as displayed with its own /Rotate, uncropped) on this page as displayed:
    /// the crop moves it, the plan's rotation turns it; points outside the crop are clamped to its edge.
    /// </summary>
    public ViewPoint FromSourcePoint(ViewPoint point)
    {
        var area = Crop ?? new PageCrop(0, 0, BaseSize.Width, BaseSize.Height);
        var x = Math.Clamp(point.X - area.Left, 0, area.Width);
        var y = Math.Clamp(point.Y - area.Top, 0, area.Height);
        return QuarterTurns switch
        {
            1 => new ViewPoint(area.Height - y, x),
            2 => new ViewPoint(area.Width - x, area.Height - y),
            3 => new ViewPoint(y, area.Width - x),
            _ => new ViewPoint(x, y),
        };
    }
}

/// <summary>
/// The document as its page plan shows it (SPEC "PagePlan — tài liệu ảo"): what the viewer, the sidebar, printing and
/// the properties dialog read. Immutable; a new one is built after every plan change (cheap: one entry per page).
/// </summary>
public sealed class VirtualDocument
{
    private readonly IReadOnlyDictionary<string, IPdfDocument> _sources;

    private VirtualDocument(PagePlan plan, IReadOnlyDictionary<string, IPdfDocument> sources, VirtualPage[] pages)
    {
        Plan = plan;
        _sources = sources;
        Pages = pages;
        PageSizes = Array.ConvertAll(pages, p => p.DisplaySize);
    }

    public PagePlan Plan { get; }

    public IReadOnlyList<VirtualPage> Pages { get; }

    /// <summary>Displayed size of every page (what layouts are built from).</summary>
    public IReadOnlyList<PdfSize> PageSizes { get; }

    public int PageCount => Pages.Count;

    /// <param name="plan">The pages.</param>
    /// <param name="sources">The opened document of every source the plan uses, by source id.</param>
    /// <exception cref="ArgumentException">A source is missing or has no such page.</exception>
    public static VirtualDocument Create(PagePlan plan, IReadOnlyDictionary<string, IPdfDocument> sources)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        var pages = new VirtualPage[plan.Count];
        for (var i = 0; i < pages.Length; i++)
        {
            var pageRef = plan[i];
            if (pageRef.BlankSize is { } blankSize)
            {
                pages[i] = new VirtualPage(pageRef, null, blankSize);
                continue;
            }

            if (!sources.TryGetValue(pageRef.SourceId, out var source) || pageRef.SourcePageIndex >= source.PageCount)
            {
                throw new ArgumentException($"Page {i + 1} refers to a source page that is not open.", nameof(sources));
            }

            pages[i] = new VirtualPage(pageRef, source, source.PageSizes[pageRef.SourcePageIndex]);
        }

        return new VirtualDocument(plan, sources, pages);
    }

    public int IndexOf(Guid pageKey) => Plan.IndexOf(pageKey);

    /// <summary>Position of the first page showing page <paramref name="sourcePageIndex"/> of <paramref name="source"/>, or -1.</summary>
    public int FirstIndexOf(IPdfDocument source, int sourcePageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (var i = 0; i < Pages.Count; i++)
        {
            if (ReferenceEquals(Pages[i].Source, source) && Pages[i].SourcePageIndex == sourcePageIndex)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The opened document of a source, or null.</summary>
    public IPdfDocument? SourceDocument(string sourceId) => _sources.GetValueOrDefault(sourceId);
}
