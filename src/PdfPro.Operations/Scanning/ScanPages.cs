using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Scanning;

/// <summary>A page of a document (0-based) and its picture as rendered in grey.</summary>
/// <param name="PageIndex">The page.</param>
/// <param name="Picture">The page as displayed (its /Rotate, cropped), rows top to bottom.</param>
/// <param name="Display">Its displayed size in points.</param>
public sealed record ScanPage(int PageIndex, GrayImage Picture, PdfSize Display);

/// <summary>What the scan tools share: rendering pages to look at them, and changing pages without touching their images.</summary>
public static class ScanPages
{
    /// <summary>Entry of the content added by the scan tools (straightening, white borders).</summary>
    public const string MarkerKey = "/PdfProScan";

    /// <summary>
    /// Renders the pages of a file in grey, one at a time through the PDFium gate (low priority), and hands each to
    /// <paramref name="measure"/> outside the gate; the results come back in page order.
    /// </summary>
    /// <param name="gate">PDFium.</param>
    /// <param name="path">The file.</param>
    /// <param name="pages">The pages (0-based); null for all.</param>
    /// <param name="dpi">The resolution.</param>
    /// <param name="measure">What is made of each page.</param>
    /// <param name="progress">Pages done of all.</param>
    /// <param name="stage">The progress stage.</param>
    /// <param name="cancellationToken">Stops between pages.</param>
    public static async Task<IReadOnlyList<T>> MeasureAsync<T>(
        PdfiumGate gate,
        string path,
        IReadOnlyList<int>? pages,
        double dpi,
        Func<ScanPage, T> measure,
        IProgress<OperationProgress> progress,
        string stage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(progress);
        PdfiumDocument? document = null;
        try
        {
            var targets = await gate.RunAsync(
                () =>
                {
                    document = PdfiumDocument.Open(gate, path);
                    var count = document.PageCount;
                    return pages is null ? [.. Enumerable.Range(0, count)] : pages.Where(p => p >= 0 && p < count).Distinct().Order().ToList();
                },
                PdfiumPriority.Low,
                cancellationToken).ConfigureAwait(false);
            var results = new List<T>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                progress.Report(new OperationProgress(i, targets.Count, stage));
                var page = targets[i];
                var scan = await gate.RunAsync(() => Render(document!, page, dpi), PdfiumPriority.Low, cancellationToken).ConfigureAwait(false);
                results.Add(await Task.Run(() => measure(scan), cancellationToken).ConfigureAwait(false));
            }

            progress.Report(new OperationProgress(targets.Count, targets.Count, stage));
            return results;
        }
        finally
        {
            if (document is { } opened)
            {
                await gate.RunAsync(opened.Dispose, PdfiumPriority.High, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A page rendered in grey at <paramref name="dpi"/> (inside the gate).</summary>
    public static ScanPage Render(PdfiumDocument document, int pageIndex, double dpi)
    {
        ArgumentNullException.ThrowIfNull(document);
        var display = document.GetPageGeometry(pageIndex).DisplaySize;
        using var bitmap = document.RenderPage(pageIndex, new PageRenderOptions { Scale = dpi / 72, Flags = PdfRenderFlags.Grayscale });
        return new ScanPage(pageIndex, GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride), display);
    }

    /// <summary>
    /// Turns everything drawn on the page clockwise by <paramref name="degrees"/> around the middle of its visible box,
    /// as the page is displayed (a scan whose lines rise to the right by that angle comes out level). The content is
    /// wrapped, not rewritten: images keep their bytes.
    /// </summary>
    public static void Straighten(PdfPage page, double degrees)
    {
        ArgumentNullException.ThrowIfNull(page);
        var box = PageStamper.Geometry(page).Box;
        var (cx, cy) = ((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);

        // Clockwise on screen is clockwise in the page's own space too (a turn keeps its sense under /Rotate).
        var m = PdfMatrix.Translation(-cx, -cy).Then(PdfMatrix.Rotation(-degrees)).Then(PdfMatrix.Translation(cx, cy));
        var contents = new PdfArray(page.Owner);
        contents.Elements.Add(NewStream(page.Owner, string.Create(CultureInfo.InvariantCulture, $"q {N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm\n"), marked: true));
        PageStamper.ContentStreams(page).ForEach(contents.Elements.Add);
        contents.Elements.Add(NewStream(page.Owner, "\nQ\n", marked: true));
        page.Elements["/Contents"] = contents;
    }

    /// <summary>Paints rectangles of the page white, over everything (the content under them stays in the file).</summary>
    /// <param name="page">The page.</param>
    /// <param name="rects">The rectangles, in the page's PDF space.</param>
    public static void PaintWhite(PdfPage page, IReadOnlyList<PdfRect> rects)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(rects);
        if (rects.Count == 0)
        {
            return;
        }

        var text = new StringBuilder("q 1 g\n");
        foreach (var rect in rects)
        {
            text.Append(CultureInfo.InvariantCulture, $"{N(rect.Left)} {N(rect.Bottom)} {N(rect.Width)} {N(rect.Height)} re f\n");
        }

        text.Append("Q\n");
        var contents = new PdfArray(page.Owner);
        contents.Elements.Add(NewStream(page.Owner, "q\n", marked: false));
        PageStamper.ContentStreams(page).ForEach(contents.Elements.Add);
        contents.Elements.Add(NewStream(page.Owner, "\nQ\n", marked: false));
        contents.Elements.Add(NewStream(page.Owner, text.ToString(), marked: true));
        page.Elements["/Contents"] = contents;
    }

    private static PdfReference NewStream(PdfDocument document, string text, bool marked)
    {
        var stream = new PdfContent(document);
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(Encoding.ASCII.GetBytes(text));
            }

            stream.CreateStream(output.ToArray());
        }

        stream.Elements.SetName("/Filter", "/FlateDecode");
        if (marked)
        {
            stream.Elements.SetBoolean(MarkerKey, true);
        }

        document.Internals.AddObject(stream);
        return stream.Reference!;
    }

    private static string N(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
