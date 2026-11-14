using System.Runtime.CompilerServices;
using PdfPro.Pdfium.Native;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

/// <summary>
/// Native handle accounting. Runs alone (the counters are process-wide) so other tests cannot open
/// documents in between.
/// </summary>
[Collection(nameof(SerialPdfiumCollection))]
public sealed class HandleLifetimeTests(PdfiumGateFixture fixture)
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenRenderReadAndDispose_LeavesNoNativeHandles()
    {
        await fixture.FlushAsync();
        var baseline = PdfiumHandleTracker.TotalOpen;

        var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.BasicVi, cancellationToken: TestToken);
        PdfiumHandleTracker.OpenCount(PdfiumHandleKind.Document).ShouldBeGreaterThan(0);
        for (var page = 0; page < document.PageCount; page++)
        {
            using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = 0.5 }, cancellationToken: TestToken);
            _ = await document.GetPageTextAsync(page, TestToken);
        }

        _ = await document.GetBookmarksAsync(TestToken);
        _ = await document.GetMetadataAsync(TestToken);
        await document.DisposeAsync();
        await fixture.FlushAsync();

        PdfiumHandleTracker.TotalOpen.ShouldBe(baseline);
    }

    [Fact]
    public async Task ReadingAnnotations_ClosesEveryAnnotationHandle()
    {
        await fixture.FlushAsync();
        var baseline = PdfiumHandleTracker.TotalOpen;

        var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.LinksAnnots, cancellationToken: TestToken);
        var found = 0;
        for (var page = 0; page < document.PageCount; page++)
        {
            found += (await document.GetPageAnnotationsAsync(page, cancellationToken: TestToken)).Count;
        }

        await fixture.FlushAsync();
        found.ShouldBeGreaterThan(0, "the sample has links");
        PdfiumHandleTracker.OpenCount(PdfiumHandleKind.Annotation).ShouldBe(0, "annotations (and the ones they link to) are closed after reading");
        await document.DisposeAsync();
        await fixture.FlushAsync();

        PdfiumHandleTracker.TotalOpen.ShouldBe(baseline);
    }

    [Fact]
    public async Task BitmapDisposedOnAnotherThread_IsReleasedOnTheGate()
    {
        await using var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.BasicVi, cancellationToken: TestToken);
        await fixture.FlushAsync();
        var bitmaps = PdfiumHandleTracker.OpenCount(PdfiumHandleKind.Bitmap);

        var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 0.25 }, cancellationToken: TestToken);
        PdfiumHandleTracker.OpenCount(PdfiumHandleKind.Bitmap).ShouldBe(bitmaps + 1);
        await Task.Run(bitmap.Dispose, TestToken);
        await fixture.FlushAsync();

        PdfiumHandleTracker.OpenCount(PdfiumHandleKind.Bitmap).ShouldBe(bitmaps);
        Should.Throw<ObjectDisposedException>(() => bitmap.Scan0);
    }

    [Fact]
    public async Task ForgottenDocument_IsReleasedByTheFinalizerOnTheGate()
    {
        await fixture.FlushAsync();
        var baseline = PdfiumHandleTracker.TotalOpen;

        await OpenRenderAndForget();
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await fixture.FlushAsync();
        }

        PdfiumHandleTracker.TotalOpen.ShouldBe(baseline);
    }

    [Fact]
    public async Task DisposedDocument_RejectsFurtherWork()
    {
        var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.BasicVi, cancellationToken: TestToken);
        await document.DisposeAsync();

        document.IsDisposed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => document.GetPageTextAsync(0, TestToken));
        await document.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task GateAffineObjects_RejectUseFromOtherThreads()
    {
        await using var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.BasicVi, cancellationToken: TestToken);
        var raw = await document.RunAsync(d => d, cancellationToken: TestToken);

        Should.Throw<InvalidOperationException>(() => raw.GetPageSize(0));
        Should.Throw<InvalidOperationException>(raw.Dispose);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task OpenRenderAndForget()
    {
        // Gate-affine objects created and dropped without Dispose: only their SafeHandle finalizers can free them.
        await fixture.Gate.RunAsync(
            () =>
            {
                var document = PdfiumDocument.Open(fixture.Gate, Samples.BasicVi);
                var page = document.LoadPage(0);
                var text = page.LoadText();
                _ = text.CharCount;
                _ = document.RenderPage(1, new PageRenderOptions { Scale = 0.25 });
            },
            cancellationToken: TestToken);
    }
}
