using System.Diagnostics;
using System.Runtime.CompilerServices;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Ocr;

/// <summary>
/// ocr.run (<see cref="OperationKind.Content"/>): scanned pages made searchable. Pages that already have text are left
/// alone, or their hidden text of an earlier OCR replaced (<see cref="ExistingTextMode"/>). Each page to read is
/// rendered by PDFium (300 DPI, grey), checked for ink (blank pages are not read), turned the right way up and
/// straightened when needed (<see cref="OcrPageReader"/>) and recognised by the OCR engine — several pages at once, as
/// many as <see cref="OcrParams.Workers"/>. The text found is laid over the page as invisible text
/// (<see cref="OcrTextLayers"/>): the page's own content, its images byte for byte, is not changed (unless asked to
/// straighten pages, which turns their content). The report (<see cref="ReportOf"/>) gives every page's outcome, mean
/// confidence, text and, when asked, hOCR. Cancelled, nothing is written.
/// </summary>
public sealed class OcrOperation(PdfiumGate gate, IOcrEngine engine) : FileContentOperation<OcrParams>
{
    public const string OperationId = "ocr.run";

    /// <summary>The page picture never exceeds this many pixels (very large pages are rendered at a lower resolution).</summary>
    public const double MaxPixels = 50_000_000;

    /// <summary>Tilts below this are not worth turning a page for.</summary>
    public const double MinStraighten = 0.2;

    private static readonly ConditionalWeakTable<OcrParams, OcrReport> Reports = new();

    public override string Id => OperationId;

    public override string Stage => "Stage_Ocr";

    /// <summary>The report of the last run made with these very parameters, or null.</summary>
    public static OcrReport? ReportOf(OcrParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Reports.TryGetValue(parameters, out var report) ? report : null;
    }

    public override async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not OcrParams request)
        {
            throw new ArgumentException($"Expected {nameof(OcrParams)}.", nameof(parameters));
        }

        var clock = Stopwatch.StartNew();
        var engineInfo = engine.Describe() ?? throw new OcrException(OcrFailure.NotInstalled);
        if (request.Languages.FirstOrDefault(l => !engineInfo.Languages.Contains(l, StringComparer.Ordinal)) is { } missing)
        {
            throw new OcrException(OcrFailure.LanguageMissing, missing);
        }

        PdfiumDocument? pdfium = null;
        try
        {
            var pages = await gate.RunAsync(
                () =>
                {
                    pdfium = PdfiumDocument.Open(gate, inputPath);
                    var validation = Validate(request, new DocumentInfo(pdfium.PageCount));
                    if (!validation.IsValid)
                    {
                        throw new OperationValidationException(Id, validation.Errors);
                    }

                    var targets = request.Pages ?? [.. Enumerable.Range(0, pdfium.PageCount)];
                    return targets.Distinct().Order().Select(p => Plan(p, pdfium.GetPageTextCounts(p), request.ExistingText)).ToList();
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);

            var toRead = pages.Where(p => p.Read).Select(p => p.PageIndex).ToList();
            var total = toRead.Count + 1;
            progress.Report(new OperationProgress(0, total, Stage));
            var readings = await ReadAsync(pdfium!, toRead, request, (done) => progress.Report(new OperationProgress(done, total, Stage)), cancellationToken).ConfigureAwait(false);
            if (toRead.Count > 0 && readings.Values.All(r => r.Error is not null))
            {
                throw readings.Values.First().Error!;
            }

            var report = await Task.Run(() => Write(inputPath, output, request, pages, readings, clock, cancellationToken), cancellationToken).ConfigureAwait(false);
            Reports.AddOrUpdate(request, report);
            progress.Report(new OperationProgress(total, total, Stage));
        }
        finally
        {
            if (pdfium is { } opened)
            {
                await gate.RunAsync(opened.Dispose, PdfiumPriority.High, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    protected override OperationValidationResult ValidateCore(OcrParams parameters, DocumentInfo document)
        => parameters.Pages is { } pages && pages.Any(p => p < 0 || p >= document.PageCount)
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), "A page is outside the document."))
            : OperationValidationResult.Success;

    /// <summary>What to do with a page given the text it already has.</summary>
    private static PagePlanItem Plan(int page, PageTextCounts counts, ExistingTextMode mode) => mode switch
    {
        ExistingTextMode.Skip => new PagePlanItem(page, Read: !counts.HasText, RemoveHidden: false),
        ExistingTextMode.Replace => new PagePlanItem(page, Read: counts.Visible == 0, RemoveHidden: counts.Visible == 0 && counts.Invisible > 0),
        _ => new PagePlanItem(page, Read: true, RemoveHidden: counts.Invisible > 0),
    };

    /// <summary>Reads the pages, several at a time; each page's picture is rendered when a worker takes it.</summary>
    private async Task<Dictionary<int, PageOutcome>> ReadAsync(PdfiumDocument document, List<int> pages, OcrParams request, Action<int> done, CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<int, PageOutcome>();
        var options = new OcrOptions(request.Languages) { Hocr = request.Hocr };
        var reader = new OcrPageReader(engine, options, request.AutoRotate, request.Deskew, request.Deskew || request.StraightenPages);
        var finished = 0;
        await Parallel.ForEachAsync(
            pages,
            new ParallelOptions { MaxDegreeOfParallelism = request.Workers, CancellationToken = cancellationToken },
            async (page, token) =>
            {
                var (render, display) = await gate.RunAsync(() => Render(document, page, request.Dpi), PdfiumPriority.Normal, token).ConfigureAwait(false);
                PageOutcome outcome;
                if (InkCoverage.DarkPercent(GrayTransforms.ShrinkToWidth(render.Image, Math.Max(1, (int)(display.Width * BlankPageDetector.Scale))).Image) <= BlankPageDetector.DefaultThresholdPercent)
                {
                    outcome = new PageOutcome(null, display, render.Image.Width, render.Image.Height, null);
                }
                else
                {
                    try
                    {
                        outcome = new PageOutcome(await reader.ReadAsync(render.Image, render.Dpi, token).ConfigureAwait(false), display, render.Image.Width, render.Image.Height, null);
                    }
                    catch (OcrException ex) when (ex.Failure is OcrFailure.Failed or OcrFailure.TimedOut)
                    {
                        outcome = new PageOutcome(null, display, render.Image.Width, render.Image.Height, ex);
                    }
                }

                lock (outcomes)
                {
                    outcomes[page] = outcome;
                    done(++finished);
                }
            }).ConfigureAwait(false);
        return outcomes;
    }

    /// <summary>The page rendered in grey for OCR, and its displayed size in points.</summary>
    private static (OcrImage Picture, PdfSize Display) Render(PdfiumDocument document, int page, int dpi)
    {
        var display = document.GetPageGeometry(page).DisplaySize;
        var scale = dpi / 72.0;
        var pixels = display.Width * scale * display.Height * scale;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
        }

        using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = scale, Flags = PdfRenderFlags.Grayscale });
        var image = GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride);
        return (new OcrImage(image, bitmap.Width * 72.0 / display.Width), display);
    }

    private OcrReport Write(string inputPath, Stream output, OcrParams request, List<PagePlanItem> pages, Dictionary<int, PageOutcome> readings, Stopwatch clock, CancellationToken cancellationToken)
    {
        using var document = DocumentContentOperation<OcrParams>.OpenForChange(inputPath);
        var layers = new OcrTextLayers(document);
        var reports = new List<OcrPageReport>();
        var removed = new List<int>();
        var straightened = new List<int>();
        foreach (var item in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Read)
            {
                reports.Add(new OcrPageReport(item.PageIndex, OcrPageOutcome.HadText, null, 0, 0, 0, string.Empty));
                continue;
            }

            var outcome = readings[item.PageIndex];
            if (outcome.Error is not null)
            {
                reports.Add(new OcrPageReport(item.PageIndex, OcrPageOutcome.Failed, null, 0, 0, 0, string.Empty));
                continue;
            }

            if (outcome.Reading is not { } reading)
            {
                reports.Add(new OcrPageReport(item.PageIndex, OcrPageOutcome.Blank, null, 0, 0, 0, string.Empty));
                continue;
            }

            var result = reading.Result;
            if (result.Words.Count == 0)
            {
                reports.Add(new OcrPageReport(item.PageIndex, OcrPageOutcome.NoText, null, 0, reading.Rotation, reading.Skew, string.Empty, result.Hocr));
                continue;
            }

            var page = document.Pages[item.PageIndex];
            if (item.RemoveHidden && InvisibleText.Remove(page))
            {
                removed.Add(item.PageIndex);
            }

            // Picture pixels → rendered pixels → displayed points (y down).
            var toDisplay = reading.ToRender.Then(PdfMatrix.Scaling(outcome.Display.Width / outcome.RenderWidth, outcome.Display.Height / outcome.RenderHeight));
            layers.Add(page, result.TextLayer, toDisplay, reading.Width, reading.Height);
            if (request.StraightenPages && Math.Abs(reading.Tilt) >= MinStraighten)
            {
                OcrTextLayers.Straighten(page, reading.Tilt);
                straightened.Add(item.PageIndex);
            }

            reports.Add(new OcrPageReport(item.PageIndex, OcrPageOutcome.Recognized, result.MeanConfidence, result.Words.Count, reading.Rotation, reading.Skew, result.Text, result.Hocr));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PdfWriting.Save(document, output);
        return new OcrReport(reports, request.ConfidenceThreshold, removed, straightened, clock.Elapsed);
    }

    private sealed record PagePlanItem(int PageIndex, bool Read, bool RemoveHidden);

    private sealed record PageOutcome(PageReading? Reading, PdfSize Display, int RenderWidth, int RenderHeight, OcrException? Error);
}
