using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Xps.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Printing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Printing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Printing;

public sealed class PrintingTests : IDisposable
{
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize A4Landscape = new(842, 595);
    private static readonly PdfSize Letter = new(612, 792);

    // US Letter with a quarter-inch margin, 600 DPI printer (rendered at the 300 DPI cap).
    private static readonly PrintSheet LetterSheet = new(new ViewSize(816, 1056), new ViewRect(24, 24, 768, 1008), 600);

    private readonly TempDirectory _temp = new();
    private readonly FakePdfDocument _document = new([A4, A4Landscape, Letter]);
    private readonly SizedPageImageRenderer _renderer = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Paginator_RendersEachPageForPrintAtTheCappedResolution()
    {
        var (size, drawn) = Sta.Run(() =>
        {
            var paginator = CreatePaginator([0]);
            var page = paginator.GetPage(0);
            return (page.Size, ImageRect(page.Visual));
        });

        var placement = PrintLayout.Fit(A4, LetterSheet.PrintableArea);
        var call = _renderer.Calls.ShouldHaveSingleItem();
        call.PageIndex.ShouldBe(0);
        call.Priority.ShouldBe(PdfiumPriority.Normal);
        call.Options.Flags.ShouldBe(PdfRenderFlags.Printing | PdfRenderFlags.Annotations);
        call.Options.ExtraQuarterTurns.ShouldBe(0);
        call.Scale.ShouldBe(placement.Scale * PdfPrintPaginator.MaxDpi / 96, 1e-9);
        size.ShouldBe(new Size(816, 1056));
        drawn.ShouldNotBeNull();
        drawn.Value.X.ShouldBe(placement.Bounds.X, 1e-6);
        drawn.Value.Y.ShouldBe(placement.Bounds.Y, 1e-6);
        drawn.Value.Width.ShouldBe(placement.Bounds.Width, 1e-6);
        drawn.Value.Height.ShouldBe(placement.Bounds.Height, 1e-6);
    }

    [Fact]
    public void Paginator_TurnsLandscapePagesOnPortraitSheets()
    {
        var drawn = Sta.Run(() => ImageRect(CreatePaginator([1]).GetPage(0).Visual));

        _renderer.Calls.ShouldHaveSingleItem().Options.ExtraQuarterTurns.ShouldBe(1);
        drawn.ShouldNotBeNull();
        drawn.Value.Height.ShouldBeGreaterThan(drawn.Value.Width);
    }

    [Fact]
    public void Paginator_PrintsTheChosenPagesInOrder()
    {
        var count = Sta.Run(() =>
        {
            var paginator = CreatePaginator([2, 0]);
            paginator.GetPage(0);
            paginator.GetPage(1);
            return paginator.PageCount;
        });

        count.ShouldBe(2);
        _renderer.Calls.Select(c => c.PageIndex).ShouldBe([2, 0]);
    }

    [Fact]
    public void Paginator_LowResolutionPrinter_IsNotUpscaled()
    {
        Sta.Run(() => CreatePaginator([0], LetterSheet with { Dpi = 150 }).GetPage(0));

        var placement = PrintLayout.Fit(A4, LetterSheet.PrintableArea);
        _renderer.Calls.ShouldHaveSingleItem().Scale.ShouldBe(placement.Scale * 150 / 96, 1e-9);
    }

    [Fact]
    public void Paginator_DamagedPage_PrintsBlankAndIsCounted()
    {
        _renderer.FailingPages.Add(1);
        var logger = new CapturingLogger<PrintingTests>();

        var (failed, blank, next) = Sta.Run(() =>
        {
            var paginator = new PdfPrintPaginator(VirtualDocuments.Of(_document), _renderer, [1, 0], LetterSheet, logger, CancellationToken.None);
            var damaged = ImageRect(paginator.GetPage(0).Visual);
            var good = ImageRect(paginator.GetPage(1).Visual);
            return (paginator.FailedPages, damaged, good);
        });

        failed.ShouldBe(1);
        blank.ShouldBeNull();
        next.ShouldNotBeNull("the job goes on after a damaged page");
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("Page 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Paginator_PrintsThePlanBlankPagesEmptyAndTurnedPagesTurned()
    {
        var plan = Core.Organizing.PagePlan.Identity(Core.Organizing.SourceIds.Original, _document.PageCount, Core.Organizing.RandomPageKeys.Instance)
            .InsertBlank(0, A4, Core.Organizing.RandomPageKeys.Instance)
            .Rotate([1], 90);

        var (blank, turned) = Sta.Run(() =>
        {
            var paginator = new PdfPrintPaginator(VirtualDocuments.Of(plan, _document), _renderer, [0, 1], LetterSheet, NullLogger.Instance, CancellationToken.None);
            return (ImageRect(paginator.GetPage(0).Visual), ImageRect(paginator.GetPage(1).Visual));
        });

        blank.ShouldBeNull("a blank page prints as an empty sheet");
        turned.ShouldNotBeNull();
        var call = _renderer.Calls.ShouldHaveSingleItem();
        call.PageIndex.ShouldBe(0);
        call.Options.ExtraQuarterTurns.ShouldBe(2, "the plan turns the page once; printing a landscape page on portrait paper turns it once more");
        call.Options.Flags.ShouldBe(PdfRenderFlags.Printing | PdfRenderFlags.Annotations);
    }

    [Fact]
    public void Paginator_PrintsTheAnnotationsMadeInTheSession_WhereTheyAre()
    {
        var red = new RgbColor(220, 0, 0);
        var square = new ShapeAnnotation("s", red, 1, "Lan", DateTimeOffset.Now, DateTimeOffset.Now, string.Empty, NoteShape.Square, new PdfRect(100, 100, 200, 150), 2, red);
        var plan = PagePlan.Identity(SourceIds.Original, _document.PageCount, RandomPageKeys.Instance).WithAnnotations(0, [square]);

        var printed = Sta.Run(() =>
        {
            var paginator = new PdfPrintPaginator(VirtualDocuments.Of(plan, _document), _renderer, [0, 1], LetterSheet, NullLogger.Instance, CancellationToken.None);
            var annotated = Geometries(VisualTreeHelper.GetDrawing(paginator.GetPage(0).Visual), Matrix.Identity)
                .Where(g => g.Drawing.Brush is SolidColorBrush { Color: var color } && color == Color.FromRgb(220, 0, 0))
                .Select(g => Rect.Transform(g.Drawing.Geometry.Bounds, g.Transform))
                .ToArray();
            var plain = Geometries(VisualTreeHelper.GetDrawing(paginator.GetPage(1).Visual), Matrix.Identity).Count();
            return (annotated, plain);
        });

        // A4 portrait on Letter portrait: not turned. The square's outline is drawn inside its box (2 points wide).
        var placement = PrintLayout.Fit(A4, LetterSheet.PrintableArea);
        var scale = placement.Scale;
        var rect = printed.annotated.ShouldHaveSingleItem();
        rect.X.ShouldBe(placement.Bounds.X + (101 * scale), 0.5);
        rect.Y.ShouldBe(placement.Bounds.Y + ((842 - 149) * scale), 0.5, "PDF y goes up, the sheet's down");
        rect.Width.ShouldBe(98 * scale, 0.5);
        printed.plain.ShouldBe(0, "a page without annotations prints only its image");
    }

    [Fact]
    public void Paginator_AfterCancellation_ReturnsBlankSheetsWithoutRendering()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var blank = Sta.Run(() =>
        {
            var paginator = new PdfPrintPaginator(VirtualDocuments.Of(_document), _renderer, [0, 1], LetterSheet, NullLogger.Instance, cancellation.Token);
            return ImageRect(paginator.GetPage(0).Visual);
        });

        blank.ShouldBeNull();
        _renderer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Runner_WritesEveryPageOnItsOwnThreadAndReportsProgress()
    {
        var path = _temp.Combine("job.xps");
        var reported = new ConcurrentQueue<int>();
        var callerThread = Environment.CurrentManagedThreadId;
        var printThread = 0;
        var apartment = ApartmentState.Unknown;

        var completed = await XpsPrintRunner.RunAsync(
            () =>
            {
                printThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                return XpsFile(path);
            },
            token => CreatePaginator([0, 1, 2], token: token),
            new InlineProgress(reported.Enqueue),
            TestContext.Current.CancellationToken);

        completed.ShouldBeTrue();
        printThread.ShouldNotBe(callerThread);
        apartment.ShouldBe(ApartmentState.STA);
        reported.ShouldNotBeEmpty();
        reported.Max().ShouldBe(3);
        var pages = Sta.Run(() =>
        {
            using var xps = new XpsDocument(path, FileAccess.Read);
            var paginator = xps.GetFixedDocumentSequence().DocumentPaginator;
            return Enumerable.Range(0, paginator.PageCount).Select(i => paginator.GetPage(i).Size).ToList();
        });
        pages.ShouldBe([new Size(816, 1056), new Size(816, 1056), new Size(816, 1056)]);
    }

    [Fact]
    public async Task Runner_Cancellation_StopsTheJob()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var renderer = new CancellingRenderer(_renderer, cancelOnCall: 2, cancellation);

        var completed = await XpsPrintRunner.RunAsync(
            () => XpsFile(_temp.Combine("cancelled.xps")),
            token => new PdfPrintPaginator(VirtualDocuments.Of(_document), renderer, [0, 1, 2, 0, 1, 2], LetterSheet, NullLogger.Instance, token),
            progress: null,
            cancellation.Token);

        completed.ShouldBeFalse();
        _renderer.Calls.Count.ShouldBeLessThan(6, "pages after the cancellation are not rendered");
    }

    [Fact]
    public async Task Runner_TargetThatCannotBeOpened_FailsTheTask()
    {
        var failure = new IOException("printer offline");

        var error = await Should.ThrowAsync<IOException>(() => XpsPrintRunner.RunAsync(
            () => throw failure,
            token => CreatePaginator([0], token: token),
            progress: null,
            TestContext.Current.CancellationToken));

        error.ShouldBeSameAs(failure);
    }

    [Fact]
    public void PrintableArea_IsClippedToTheSheet()
    {
        var paper = new ViewSize(816, 1056);

        PrintDialogService.PrintableArea(paper, new ViewRect(24, 24, 768, 1008)).ShouldBe(new ViewRect(24, 24, 768, 1008));
        PrintDialogService.PrintableArea(paper, new ViewRect(24, 24, 1000, 1000)).ShouldBe(new ViewRect(24, 24, 792, 1000));
        PrintDialogService.PrintableArea(paper, null).ShouldBe(new ViewRect(0, 0, 816, 1056));
        PrintDialogService.PrintableArea(paper, new ViewRect(900, 0, 100, 100)).ShouldBe(new ViewRect(0, 0, 816, 1056), "an area off the sheet is ignored");
    }

    private static XpsTarget XpsFile(string path)
    {
        var xps = new XpsDocument(path, FileAccess.ReadWrite);
        return new XpsTarget(XpsDocument.CreateXpsDocumentWriter(xps), null, xps);
    }

    /// <summary>Where the page image was drawn on the sheet, or null for a blank sheet.</summary>
    /// <summary>The shapes of a drawing with the transforms of the groups around them.</summary>
    private static IEnumerable<(GeometryDrawing Drawing, Matrix Transform)> Geometries(Drawing? drawing, Matrix transform)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                var combined = (group.Transform?.Value ?? Matrix.Identity) * transform;
                foreach (var child in group.Children)
                {
                    foreach (var found in Geometries(child, combined))
                    {
                        yield return found;
                    }
                }

                break;
            case GeometryDrawing geometry:
                yield return (geometry, transform);
                break;
        }
    }

    private static Rect? ImageRect(Visual visual)
    {
        var drawing = VisualTreeHelper.GetDrawing(visual);
        return drawing is null || drawing.Children.Count == 0 ? null : drawing.Bounds;
    }

    private PdfPrintPaginator CreatePaginator(IReadOnlyList<int> pages, PrintSheet? sheet = null, CancellationToken token = default)
        => new(VirtualDocuments.Of(_document), _renderer, pages, sheet ?? LetterSheet, NullLogger.Instance, token);

    /// <summary>Reports synchronously on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Cancels the job while it renders the given call, like a user pressing cancel mid-page.</summary>
    private sealed class CancellingRenderer(IPageImageRenderer inner, int cancelOnCall, CancellationTokenSource cancellation) : IPageImageRenderer
    {
        private int _calls;

        public Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, PageRenderOptions options, PdfiumPriority priority, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == cancelOnCall)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return inner.RenderAsync(document, pageIndex, options, priority, cancellationToken);
        }
    }
}
