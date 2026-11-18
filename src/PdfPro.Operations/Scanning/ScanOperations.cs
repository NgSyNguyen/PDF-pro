using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Scanning;

/// <param name="Pages">The blank pages to remove ("2, 5-6").</param>
public sealed record RemoveBlankPagesParams([property: Required] string Pages);

/// <summary>How the scan tools look at a file (<see cref="IMeasuringOperation"/>); each tool uses what it needs.</summary>
/// <param name="Pages">The pages (0-based) to look at, or null for all (tilt, borders).</param>
/// <param name="MinDegrees">Tilts smaller than this are not reported.</param>
/// <param name="Languages">The languages an unsure page is read in to tell its orientation, or null for Vietnamese.</param>
/// <param name="Workers">How many pages are read at once to tell their orientation.</param>
public sealed record ScanMeasureParams(IReadOnlyList<int>? Pages = null, double MinDegrees = DeskewOperation.DefaultMinDegrees, IReadOnlyList<string>? Languages = null, int Workers = 1)
{
    internal static ScanMeasureParams Of(object parameters)
        => parameters as ScanMeasureParams ?? throw new ArgumentException("Scan tools are measured with ScanMeasureParams.", nameof(parameters));

    internal static PdfiumGate Need(PdfiumGate? gate)
        => gate ?? throw new InvalidOperationException("Measuring pages needs PDFium: the operation was built without it.");
}

/// <summary>A page (0-based) and how many degrees it is turned clockwise.</summary>
public sealed record PageTurn(int Page, int Degrees);

/// <param name="Turns">The pages to turn and by how much.</param>
public sealed record AutoRotateParams([property: Required, MinLength(1)] IReadOnlyList<PageTurn> Turns);

/// <summary>A page (0-based) and how its lines rise to the right, in degrees.</summary>
public sealed record PageAngle(int Page, double Degrees);

/// <param name="Pages">The pages to straighten and their tilt.</param>
public sealed record DeskewParams([property: Required, MinLength(1)] IReadOnlyList<PageAngle> Pages);

/// <summary>The dark bands along the edges of a page (0-based), as shares of its displayed width and height.</summary>
public sealed record PageBorders(int Page, double Left, double Top, double Right, double Bottom)
{
    public bool IsEmpty => Left <= 0 && Top <= 0 && Right <= 0 && Bottom <= 0;
}

/// <param name="Pages">The pages and their bands.</param>
public sealed record CleanBordersParams([property: Required, MinLength(1)] IReadOnlyList<PageBorders> Pages);

/// <summary>
/// scan.removeBlankPages: removes the blank pages found by <see cref="MeasureAsync"/> (separator sheets, empty backs
/// of two-sided scans) — a page operation, one undoable step. Blank is what <see cref="BlankPageDetector"/> says: dark
/// pixels at 54 DPI inside 5% margins and outside dark edge bands (scanner borders, a book's spine).
/// </summary>
public sealed class RemoveBlankPagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader, PdfiumGate? gate = null)
    : PlanOperation<RemoveBlankPagesParams>(materializer, loader), IMeasuringOperation
{
    public const string OperationId = "scan.removeBlankPages";

    public const string Stage = "Stage_FindBlankPages";

    public override string Id => OperationId;

    Type IMeasuringOperation.MeasureType => typeof(ScanMeasureParams);

    Type IMeasuringOperation.MeasurementType => typeof(IReadOnlyList<double>);

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ScanMeasureParams.Of(parameters);
        return await MeasureAsync(ScanMeasureParams.Need(gate), input, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The share of dark pixels (in %) of every page of a file (the document as displayed).</summary>
    public static Task<IReadOnlyList<double>> MeasureAsync(PdfiumGate gate, string path, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => ScanPages.MeasureAsync(gate, path, null, BlankPageDetector.Scale * 72, page => InkCoverage.DarkPercent(page.Picture), progress, Stage, cancellationToken);

    protected override OperationValidationResult ValidateCore(RemoveBlankPagesParams parameters, DocumentInfo document)
    {
        var pages = CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));
        return !pages.IsValid || PageRanges.Parse(parameters.Pages, document.PageCount).Count < document.PageCount
            ? pages
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), "A document must keep at least one page."));
    }

    protected override PagePlan Apply(PagePlan plan, RemoveBlankPagesParams parameters, PlanEditContext context)
        => plan.Delete(PageRanges.Parse(parameters.Pages, plan.Count));
}

/// <summary>
/// scan.autoRotate: turns pages that were scanned sideways or upside down the right way up — a page operation (their
/// /Rotate, nothing redrawn), one undoable step. <see cref="DetectAsync"/> asks the OCR engine's orientation
/// detection and, unless it is sure, reads the page both ways and keeps the more confident reading
/// (<see cref="OcrPageReader"/>); blank pages are left alone.
/// </summary>
public sealed class AutoRotateOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader, PdfiumGate? gate = null, IOcrEngine? ocr = null)
    : PlanOperation<AutoRotateParams>(materializer, loader), IMeasuringOperation
{
    public const string OperationId = "scan.autoRotate";

    public const string Stage = "Stage_DetectOrientation";

    private const int Dpi = 300;

    public override string Id => OperationId;

    Type IMeasuringOperation.MeasureType => typeof(ScanMeasureParams);

    Type IMeasuringOperation.MeasurementType => typeof(IReadOnlyList<PageTurn>);

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var measure = ScanMeasureParams.Of(parameters);
        return await DetectAsync(
            ScanMeasureParams.Need(gate),
            ocr ?? throw new OcrException(OcrFailure.NotInstalled),
            input,
            measure.Languages is { Count: > 0 } languages ? languages : ["vie"],
            Math.Clamp(measure.Workers, 1, Environment.ProcessorCount),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pages of a file (the document as displayed) that are turned, and the turn that makes them upright.</summary>
    /// <param name="gate">PDFium.</param>
    /// <param name="engine">The OCR engine (with orientation data).</param>
    /// <param name="path">The file.</param>
    /// <param name="languages">The languages a page is read in to check an unsure detection.</param>
    /// <param name="workers">How many pages are looked at at once.</param>
    /// <param name="progress">Pages done of all.</param>
    /// <param name="cancellationToken">Stops.</param>
    /// <exception cref="OcrException">The engine is not installed, has no orientation data, or failed.</exception>
    public static async Task<IReadOnlyList<PageTurn>> DetectAsync(
        PdfiumGate gate,
        IOcrEngine engine,
        string path,
        IReadOnlyList<string> languages,
        int workers,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(progress);
        if (engine.Describe() is not { } info)
        {
            throw new OcrException(OcrFailure.NotInstalled);
        }

        if (!info.DetectsOrientation)
        {
            throw new OcrException(OcrFailure.LanguageMissing, "osd");
        }

        var reading = languages.Where(info.Languages.Contains).ToList();
        if (reading.Count == 0)
        {
            reading.Add(info.Languages[0]);
        }

        var reader = new OcrPageReader(engine, new OcrOptions(reading), autoRotate: true, deskew: false, measureTilt: false);
        PdfiumDocument? document = null;
        try
        {
            var count = await gate.RunAsync(() => (document = PdfiumDocument.Open(gate, path)).PageCount, PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
            var turns = new List<PageTurn>();
            var finished = 0;
            progress.Report(new OperationProgress(0, count, Stage));
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = cancellationToken },
                async (page, token) =>
                {
                    var scan = await gate.RunAsync(() => ScanPages.Render(document!, page, Dpi), PdfiumPriority.Normal, token).ConfigureAwait(false);
                    var turn = await TurnOfAsync(engine, reader, scan, token).ConfigureAwait(false);
                    lock (turns)
                    {
                        if (turn != 0)
                        {
                            turns.Add(new PageTurn(page, turn));
                        }

                        progress.Report(new OperationProgress(++finished, count, Stage));
                    }
                }).ConfigureAwait(false);
            return [.. turns.OrderBy(t => t.Page)];
        }
        finally
        {
            if (document is { } opened)
            {
                await gate.RunAsync(opened.Dispose, PdfiumPriority.High, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    protected override OperationValidationResult ValidateCore(AutoRotateParams parameters, DocumentInfo document)
        => parameters.Turns.Any(t => t.Page < 0 || t.Page >= document.PageCount || t.Degrees is not (90 or 180 or 270))
            || parameters.Turns.Select(t => t.Page).Distinct().Count() != parameters.Turns.Count
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Turns), "Each turn needs a page of the document, once, and 90, 180 or 270 degrees."))
            : OperationValidationResult.Success;

    protected override PagePlan Apply(PagePlan plan, AutoRotateParams parameters, PlanEditContext context)
        => parameters.Turns.Aggregate(plan, (current, turn) => current.Rotate([turn.Page], turn.Degrees));

    /// <summary>The clockwise turn that makes a page upright: 0 when it is, when it is blank, or when no reading says otherwise.</summary>
    private static async Task<int> TurnOfAsync(IOcrEngine engine, OcrPageReader reader, ScanPage scan, CancellationToken cancellationToken)
    {
        if (InkCoverage.DarkPercent(GrayTransforms.ShrinkToWidth(scan.Picture, Math.Max(1, (int)(scan.Display.Width * BlankPageDetector.Scale))).Image) <= BlankPageDetector.DefaultThresholdPercent)
        {
            return 0;
        }

        var picture = new OcrImage(scan.Picture, Dpi);
        if (await engine.DetectOrientationAsync(picture, cancellationToken).ConfigureAwait(false) is not { Rotate: not 0 } orientation)
        {
            return 0;
        }

        if (orientation.Confidence >= OcrPageReader.SureOrientation)
        {
            return orientation.Rotate;
        }

        var upright = await reader.ReadTurnedAsync(scan.Picture, Dpi, 0, cancellationToken).ConfigureAwait(false);
        var turned = await reader.ReadTurnedAsync(scan.Picture, Dpi, orientation.Rotate, cancellationToken).ConfigureAwait(false);
        return turned.Score > upright.Score ? orientation.Rotate : 0;
    }
}

/// <summary>
/// scan.deskew: straightens tilted pages — their content is turned around the middle of the page by the tilt
/// <see cref="MeasureAsync"/> found (<see cref="SkewDetector"/>); images keep their bytes. A content operation, one
/// undoable step.
/// </summary>
public sealed class DeskewOperation(PdfiumGate? gate = null) : DocumentContentOperation<DeskewParams>, IMeasuringOperation
{
    public const string OperationId = "scan.deskew";

    /// <summary>Tilts smaller than this are not worth turning a page for.</summary>
    public const double DefaultMinDegrees = 0.2;

    /// <summary>Turns larger than this are refused (not a tilt: a turned page).</summary>
    public const double MaxDegrees = 15;

    private const double Dpi = 120;

    public override string Id => OperationId;

    public override string Stage => "Stage_Deskew";

    Type IMeasuringOperation.MeasureType => typeof(ScanMeasureParams);

    Type IMeasuringOperation.MeasurementType => typeof(IReadOnlyList<PageAngle>);

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var measure = ScanMeasureParams.Of(parameters);
        return await MeasureAsync(ScanMeasureParams.Need(gate), input, measure.Pages, measure.MinDegrees, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pages of a file (the document as displayed) tilted by at least <paramref name="minDegrees"/>, and their tilt.</summary>
    public static async Task<IReadOnlyList<PageAngle>> MeasureAsync(PdfiumGate gate, string path, IReadOnlyList<int>? pages, double minDegrees, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var angles = await ScanPages.MeasureAsync(
            gate,
            path,
            pages,
            Dpi,
            page => SkewDetector.Estimate(page.Picture) is { IsReliable: true } estimate && Math.Abs(estimate.Degrees) >= minDegrees
                ? new PageAngle(page.PageIndex, estimate.Degrees)
                : null,
            progress,
            "Stage_Deskew",
            cancellationToken).ConfigureAwait(false);
        return [.. angles.OfType<PageAngle>()];
    }

    protected override OperationValidationResult ValidateCore(DeskewParams parameters, DocumentInfo document)
        => parameters.Pages.Any(p => p.Page < 0 || p.Page >= document.PageCount || !double.IsFinite(p.Degrees) || Math.Abs(p.Degrees) > MaxDegrees)
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), $"Each page must be in the document and tilted by at most {MaxDegrees}°."))
            : OperationValidationResult.Success;

    protected override void Apply(PdfDocument document, string inputPath, DeskewParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        foreach (var page in parameters.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanPages.Straighten(document.Pages[page.Page], page.Degrees);
        }
    }
}

/// <summary>
/// scan.cleanBorders: paints white the dark bands along the edges of scanned pages (the scanner's black borders, the
/// shadow of a book's spine) that <see cref="MeasureAsync"/> found (<see cref="ScanBorders"/>) — over the page, so the
/// images keep their bytes. A content operation, one undoable step.
/// </summary>
public sealed class CleanBordersOperation(PdfiumGate? gate = null) : DocumentContentOperation<CleanBordersParams>, IMeasuringOperation
{
    public const string OperationId = "scan.cleanBorders";

    private const double Dpi = 100;

    public override string Id => OperationId;

    public override string Stage => "Stage_CleanBorders";

    Type IMeasuringOperation.MeasureType => typeof(ScanMeasureParams);

    Type IMeasuringOperation.MeasurementType => typeof(IReadOnlyList<PageBorders>);

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var measure = ScanMeasureParams.Of(parameters);
        return await MeasureAsync(ScanMeasureParams.Need(gate), input, measure.Pages, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pages of a file (the document as displayed) with dark bands along their edges, and the bands.</summary>
    public static async Task<IReadOnlyList<PageBorders>> MeasureAsync(PdfiumGate gate, string path, IReadOnlyList<int>? pages, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var borders = await ScanPages.MeasureAsync(
            gate,
            path,
            pages,
            Dpi,
            page =>
            {
                var (w, h) = (page.Picture.Width, page.Picture.Height);
                var bands = ScanBorders.Find(page.Picture);

                // One pixel more covers the soft edge of the band.
                double Share(int pixels, int size) => pixels == 0 ? 0 : Math.Min(ScanBorders.MaxShare, (pixels + 1.0) / size);
                return new PageBorders(page.PageIndex, Share(bands.Left, w), Share(bands.Top, h), Share(bands.Right, w), Share(bands.Bottom, h));
            },
            progress,
            "Stage_CleanBorders",
            cancellationToken).ConfigureAwait(false);
        return [.. borders.Where(b => !b.IsEmpty)];
    }

    protected override OperationValidationResult ValidateCore(CleanBordersParams parameters, DocumentInfo document)
        => parameters.Pages.Any(p => p.Page < 0 || p.Page >= document.PageCount || Bad(p.Left) || Bad(p.Top) || Bad(p.Right) || Bad(p.Bottom))
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), "Each page must be in the document, its bands between 0 and a quarter of the page."))
            : OperationValidationResult.Success;

    protected override void Apply(PdfDocument document, string inputPath, CleanBordersParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        foreach (var borders in parameters.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[borders.Page];
            var mapper = new PageCoordinateMapper(PageStamper.Geometry(page), 1);
            var size = mapper.ViewSize;
            var bands = new List<ViewRect>();
            if (borders.Left > 0)
            {
                bands.Add(new ViewRect(0, 0, borders.Left * size.Width, size.Height));
            }

            if (borders.Top > 0)
            {
                bands.Add(new ViewRect(0, 0, size.Width, borders.Top * size.Height));
            }

            if (borders.Right > 0)
            {
                bands.Add(new ViewRect(size.Width * (1 - borders.Right), 0, borders.Right * size.Width, size.Height));
            }

            if (borders.Bottom > 0)
            {
                bands.Add(new ViewRect(0, size.Height * (1 - borders.Bottom), size.Width, borders.Bottom * size.Height));
            }

            ScanPages.PaintWhite(page, [.. bands.Select(mapper.ScreenRectToPdf)]);
        }
    }

    private static bool Bad(double share) => !double.IsFinite(share) || share < 0 || share > ScanBorders.MaxShare + 1e-9;
}
