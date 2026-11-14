using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Splitting;

/// <summary>
/// Finds blank pages (separator sheets, empty backs of a two-sided scan): the page is rendered in grey at low
/// resolution and the share of dark pixels is measured by <see cref="InkCoverage"/> — 5% of each side left out (punch
/// holes, scanner edges), and so are dark bands along the edges (black scanner borders, the shadow of a book's spine).
/// Rendering is the slow part, so <see cref="MeasureAsync"/> keeps the shares and <see cref="IsBlank"/> applies any
/// threshold to them instantly.
/// </summary>
public static class BlankPageDetector
{
    /// <summary>
    /// Default "blank" threshold: dark pixels cover at most 0.1% of the page. On samples/scan-vi.pdf separator sheets
    /// with dust and bleed-through measure about 0.02% and the sparsest text page (two lines) about 0.36%.
    /// </summary>
    public const double DefaultThresholdPercent = 0.1;

    /// <summary>Resource key of the progress stage.</summary>
    public const string Stage = "Stage_SplitBlankPages";

    /// <summary>Render resolution in pixels per point (0.75 = 54 DPI): enough for text strokes, blurs scanner dust.</summary>
    internal const double Scale = 0.75;

    /// <summary>The share of dark pixels (in %) of every page of the document; blank pages of the plan give 0.</summary>
    public static async Task<IReadOnlyList<double>> MeasureAsync(SplitDocument document, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(progress);
        var shares = new double[document.Plan.Count];
        for (var i = 0; i < shares.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, shares.Length, Stage));
            if (document.SourceOf(i) is { } source)
            {
                shares[i] = await MeasurePageAsync(source.Document, source.PageIndex, document.Plan[i].CropBoxOverride, cancellationToken).ConfigureAwait(false);
            }
        }

        progress.Report(new OperationProgress(shares.Length, shares.Length, Stage));
        return shares;
    }

    /// <summary>The share of dark pixels (in %) of one page, as displayed with its own /Rotate and the plan's crop.</summary>
    public static async Task<double> MeasurePageAsync(IPdfDocument document, int pageIndex, PageCrop? crop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var size = document.PageSizes[pageIndex];
        var full = new PixelRect(0, 0, PixelSize.FromPoints(size.Width, Scale), PixelSize.FromPoints(size.Height, Scale));
        var area = crop is not { } kept
            ? full
            : PixelRect.Intersect(full, new PixelRect(
                (int)Math.Floor(kept.Left * Scale),
                (int)Math.Floor(kept.Top * Scale),
                (int)Math.Ceiling(kept.Width * Scale),
                (int)Math.Ceiling(kept.Height * Scale)));
        if (area.IsEmpty)
        {
            return 0;
        }

        var options = new PageRenderOptions { Scale = Scale, Flags = PdfRenderFlags.Grayscale | PdfRenderFlags.Annotations, Region = area };
        using var bitmap = await document.RenderPageAsync(pageIndex, options, PdfiumPriority.Low, cancellationToken).ConfigureAwait(false);
        return InkCoverage.DarkPercent(GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride));
    }

    /// <summary>True when a page with <paramref name="darkPercent"/> ink is blank at <paramref name="thresholdPercent"/>.</summary>
    public static bool IsBlank(double darkPercent, double thresholdPercent) => darkPercent <= thresholdPercent;

    /// <summary>The positions of the blank pages.</summary>
    public static IReadOnlySet<int> BlankPages(IReadOnlyList<double> darkPercents, double thresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(darkPercents);
        return Enumerable.Range(0, darkPercents.Count).Where(i => IsBlank(darkPercents[i], thresholdPercent)).ToHashSet();
    }
}
