using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Security;

/// <summary>Areas to remove on one page (1-based), in the page's PDF space.</summary>
public sealed record RedactionArea(int Page, IReadOnlyList<PdfRect> Rects);

/// <summary>How redacted pages are rewritten.</summary>
public enum RedactionMode
{
    /// <summary>
    /// Advanced: only what lies under the areas goes; the rest of the page keeps its text (selectable, searchable) and
    /// drawings. A page that cannot be rewritten exactly, or on which any letter is found under an area afterwards, is
    /// redacted as a picture instead.
    /// </summary>
    Vector,

    /// <summary>
    /// Safe (the default): every page with an area becomes a picture of itself without the areas (nothing of its text
    /// is left).
    /// </summary>
    Raster,
}

/// <param name="Areas">What to remove.</param>
/// <param name="Fill">The colour of the boxes left in place of the removed content (black by default).</param>
/// <param name="Label">Words written in every box ("Đã xóa theo quy định"), or null.</param>
/// <param name="Mode">Turn the pages into pictures (default) or keep their other text and drawings.</param>
public sealed record RedactParams(
    [property: Required] IReadOnlyList<RedactionArea> Areas,
    RgbColor? Fill = null,
    string? Label = null,
    RedactionMode Mode = RedactionMode.Raster);

/// <summary>What a redaction did, for the user: the pages redacted, those turned into pictures, and what else went.</summary>
/// <param name="Areas">Rectangles removed.</param>
/// <param name="Pages">The pages redacted (1-based).</param>
/// <param name="PicturePages">Of those, the pages that are now pictures of themselves (all of them in the raster mode).</param>
/// <param name="Annotations">Comments and links removed because they were over an area or named what was removed.</param>
/// <param name="Fields">Form fields removed for the same reasons.</param>
/// <param name="Bookmarks">Bookmarks removed because their title named what was removed.</param>
/// <param name="Metadata">Document information entries removed ("Author", "Keywords"…).</param>
/// <param name="Xmp">The XMP metadata was removed.</param>
/// <param name="Structure">Texts of the tag structure (alternate texts, titles) removed because they named what was removed.</param>
public sealed record RedactionReport(
    int Areas,
    IReadOnlyList<int> Pages,
    IReadOnlyList<int> PicturePages,
    int Annotations,
    int Fields,
    int Bookmarks,
    IReadOnlyList<string> Metadata,
    bool Xmp,
    int Structure);

/// <summary>
/// "security.redact" — true redaction (SPEC P8 B), a content change. In the raster mode (the default) each page with an
/// area is rendered by PDFium at 300 DPI without its annotations, painted over and replaced by that picture. In the
/// vector mode PDFium takes out of each page only what lies under its areas (characters, parts of lines and shapes,
/// pixels of pictures), draws the boxes and checks the page it wrote: no character under an area, every other one where
/// it was, the page looking as before elsewhere — a page that fails becomes a picture instead. Annotations, links, form
/// fields, bookmarks, metadata and tag texts that show what was removed go too. Afterwards an independent extractor
/// (PdfPig) reads the redacted pages: a vector page with a letter under an area becomes a picture too; a letter left on
/// a picture page stops the operation, and nothing is written. Labels are drawn last. The parameters hold only
/// rectangles, never the removed text.
/// </summary>
public sealed class RedactOperation(PdfiumGate gate) : FileContentOperation<RedactParams>
{
    public const string OperationId = "security.redact";

    public const int MaxLabelLength = 100;

    private const string ImageName = "/PdfProRedacted";

    private static readonly ConditionalWeakTable<RedactParams, RedactionReport> Reports = new();

    public override string Id => OperationId;

    public override string Stage => "Stage_Redact";

    /// <summary>The report of the last redaction made with these very parameters, or null.</summary>
    public static RedactionReport? ReportOf(RedactParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Reports.TryGetValue(parameters, out var report) ? report : null;
    }

    public override async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not RedactParams request)
        {
            throw new ArgumentException($"Expected {nameof(RedactParams)}.", nameof(parameters));
        }

        var areas = request.Areas
            .GroupBy(a => a.Page - 1)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PdfRect>)[.. g.SelectMany(a => a.Rects)]);
        var fill = request.Fill ?? RgbColor.Black;
        var total = areas.Count + 3;
        progress.Report(new OperationProgress(0, total, Stage));

        // PDFium: what each page shows under its areas; the pages rewritten keeping their text, or as pictures.
        var (pages, rewritten) = await gate.RunAsync(
            () => Redact(inputPath, request, areas, fill, progress, total, cancellationToken),
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);

        // PDFsharp: picture pages replace theirs, then what else showed the removed content goes.
        var (bytes, cleanup) = await Task.Run(() => Write(inputPath, rewritten, pages, areas, fill, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress.Report(new OperationProgress(total - 2, total, Stage));

        // The mandatory check, with an extractor that is not PDFium; the labels are ours and come after it. A page that
        // kept its text and still shows a letter under an area becomes a picture, and the check runs again.
        var leaks = await Task.Run(() => Leaks(bytes, pages, areas), cancellationToken).ConfigureAwait(false);
        if (leaks.Count > 0 && leaks.All(i => pages.Single(p => p.PageIndex == i).Raster is null))
        {
            pages = await gate.RunAsync(() => AsPictures(inputPath, pages, leaks, areas, fill), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
            (bytes, cleanup) = await Task.Run(() => Write(inputPath, rewritten, pages, areas, fill, cancellationToken), cancellationToken).ConfigureAwait(false);
            leaks = await Task.Run(() => Leaks(bytes, pages, areas), cancellationToken).ConfigureAwait(false);
        }

        if (leaks.Count > 0)
        {
            throw new InvalidOperationException($"Text is left under an area of redacted page {leaks[0] + 1}; the redaction was not written.");
        }

        progress.Report(new OperationProgress(total - 1, total, Stage));
        if (!string.IsNullOrWhiteSpace(request.Label))
        {
            bytes = await Task.Run(() => Label(bytes, areas, fill, TextNormalizer.ToNfc(request.Label.Trim())), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        Reports.AddOrUpdate(request, new RedactionReport(
            areas.Values.Sum(r => r.Count),
            [.. pages.Select(p => p.PageIndex + 1)],
            [.. pages.Where(p => p.Raster is not null).Select(p => p.PageIndex + 1)],
            cleanup.Annotations,
            cleanup.Fields,
            cleanup.Bookmarks,
            cleanup.Metadata,
            cleanup.Xmp,
            cleanup.Structure));
        progress.Report(new OperationProgress(total, total, Stage));
    }

    protected override OperationValidationResult ValidateCore(RedactParams parameters, DocumentInfo document)
    {
        if (parameters.Areas is null || parameters.Areas.Count == 0)
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Areas), "At least one area is needed."));
        }

        var errors = new List<OperationValidationError>();
        for (var i = 0; i < parameters.Areas.Count; i++)
        {
            var member = $"{nameof(parameters.Areas)}[{i}]";
            var area = parameters.Areas[i];
            if (area is null || area.Page < 1 || area.Page > document.PageCount)
            {
                errors.Add(new OperationValidationError(member, $"The page must be between 1 and {document.PageCount}."));
            }
            else if (area.Rects is not { Count: > 0 } rects || rects.Any(r => !double.IsFinite(r.Left + r.Bottom + r.Right + r.Top) || r.Width <= 0 || r.Height <= 0))
            {
                errors.Add(new OperationValidationError(member, "An area needs rectangles with a size."));
            }
        }

        if (parameters.Label is { } label && (label.Length > MaxLabelLength || label.Any(char.IsControl)))
        {
            errors.Add(new OperationValidationError(nameof(parameters.Label), $"A label has at most {MaxLabelLength} characters, on one line."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    /// <summary>
    /// On the PDFium thread: reads the text under the areas of each page, rewrites the pages keeping their text (vector
    /// mode) into a copy of the document, and renders as pictures the pages that are not rewritten that way — from the
    /// document as it was.
    /// </summary>
    private (List<RedactedPage> Pages, byte[]? Rewritten) Redact(
        string inputPath,
        RedactParams request,
        IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas,
        RgbColor fill,
        IProgress<OperationProgress> progress,
        int total,
        CancellationToken cancellationToken)
    {
        using var original = Open(inputPath);
        var validation = Validate(request, new DocumentInfo(original.PageCount));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        var texts = areas.ToDictionary(a => a.Key, a => RedactionRaster.TextUnder(original.GetPageTextLayer(a.Key), a.Value));
        var pictures = new HashSet<int>(request.Mode == RedactionMode.Raster ? areas.Keys : []);
        byte[]? rewritten = null;
        if (request.Mode == RedactionMode.Vector)
        {
            using var copy = Open(inputPath);
            foreach (var (index, rects) in areas.OrderBy(a => a.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var needles = RedactionCleanup.Needles(texts[index]);
                if (!copy.RedactPageKeepingVectors(index, rects, fill, value => RedactionCleanup.Names(value, needles)).Done)
                {
                    pictures.Add(index);
                }
            }

            using var stream = new MemoryStream();
            copy.SaveCopy(stream);
            rewritten = stream.ToArray();
        }

        var done = 0;
        var pages = new List<RedactedPage>();
        foreach (var (index, rects) in areas.OrderBy(a => a.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(new RedactedPage(index, texts[index], pictures.Contains(index) ? RedactionRaster.Render(original, index, rects, fill) : null));
            progress.Report(new OperationProgress(++done, total, Stage));
        }

        return (pages, rewritten);
    }

    private PdfiumDocument Open(string path)
    {
        try
        {
            return PdfiumDocument.Open(gate, path);
        }
        catch (PdfDocumentException ex)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    /// <summary>
    /// PDFsharp, on the rewritten copy (or the input in the raster mode): picture pages replace theirs, rewritten pages
    /// lose their old thumbnails and private data, then the cleanup.
    /// </summary>
    private static (byte[] Bytes, CleanupResult Cleanup) Write(
        string inputPath,
        byte[]? rewritten,
        IReadOnlyList<RedactedPage> pages,
        IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas,
        RgbColor fill,
        CancellationToken cancellationToken)
    {
        using var document = rewritten is null ? DocumentContentOperation<RedactParams>.OpenForChange(inputPath) : OpenForChange(rewritten);
        foreach (var redacted in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[redacted.PageIndex];
            if (redacted.Raster is { } raster)
            {
                ReplaceContent(document, page, raster, areas[redacted.PageIndex], fill);
            }

            // Old pictures of the page and private data of the program that made it could show the content.
            foreach (var key in new[] { "/Thumb", "/PieceInfo", "/Metadata" })
            {
                page.Elements.Remove(key);
            }
        }

        var cleanup = RedactionCleanup.Apply(document, areas, [.. pages.SelectMany(p => p.RemovedTexts)]);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return (stream.ToArray(), cleanup);
    }

    private static PdfDocument OpenForChange(byte[] bytes)
    {
        try
        {
            return PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            throw new InvalidDataException("The rewritten document cannot be read.", ex);
        }
    }

    /// <summary>The label in the boxes of every redacted page, as a last step.</summary>
    private static byte[] Label(byte[] bytes, IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas, RgbColor fill, string label)
    {
        using var document = OpenForChange(bytes);
        foreach (var (index, rects) in areas)
        {
            DrawLabels(document.Pages[index], rects, fill, label);
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>The page shows its picture and the boxes; its old content, resources and thumbnail are gone.</summary>
    private static void ReplaceContent(PdfDocument document, PdfPage page, RasterPage raster, IReadOnlyList<PdfRect> rects, RgbColor fill)
    {
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", raster.Width);
        image.Elements.SetInteger("/Height", raster.Height);
        image.Elements.SetName("/ColorSpace", raster.IsGray ? "/DeviceGray" : "/DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.CreateStream(raster.FlateData);
        image.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(image);

        // The picture covers the visible box as shown: its unit square goes to the shown page, then to PDF space.
        var geometry = PageStamper.Geometry(page);
        var shown = geometry.DisplaySize;
        var m = new PdfMatrix(shown.Width, 0, 0, -shown.Height, 0, shown.Height).Then(new PageCoordinateMapper(geometry, 1).ScreenToPdfMatrix);
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"q {N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm {ImageName} Do Q\n");
        content.Append(CultureInfo.InvariantCulture, $"q {N(fill.R / 255.0)} {N(fill.G / 255.0)} {N(fill.B / 255.0)} rg\n");
        foreach (var rect in rects)
        {
            content.Append(CultureInfo.InvariantCulture, $"{N(rect.Left)} {N(rect.Bottom)} {N(rect.Width)} {N(rect.Height)} re\n");
        }

        content.Append("f Q\n");
        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        document.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference!;

        var xObjects = new PdfDictionary(document);
        xObjects.Elements[ImageName] = image.Reference!;
        var resources = new PdfDictionary(document);
        resources.Elements["/XObject"] = xObjects;
        page.Elements["/Resources"] = resources;

        // Old pictures of the page, private data of the program that made it and page-level metadata could show the content.
        foreach (var key in new[] { "/Thumb", "/PieceInfo", "/Metadata", "/StructParents", "/B" })
        {
            page.Elements.Remove(key);
        }
    }

    /// <summary>The label in every box, upright as the page is shown, white on dark boxes; left out where it cannot fit.</summary>
    private static void DrawLabels(PdfPage page, IReadOnlyList<PdfRect> rects, RgbColor fill, string label)
    {
        FontLibrary.Register();
        var geometry = PageStamper.Geometry(page);
        var mapper = new PageCoordinateMapper(geometry, 1);
        var dark = (0.299 * fill.R) + (0.587 * fill.G) + (0.114 * fill.B) < 140;
        var brush = dark ? XBrushes.White : XBrushes.Black;
        var placements = new List<FormPlacement>();
        foreach (var rect in rects)
        {
            var box = mapper.PdfRectToScreen(rect);
            var size = Math.Min(10, box.Height * 0.6);
            var font = new XFont(FontFamilies.NotoSans, size, XFontStyleEx.Regular);
            var width = MeasureWidth(label, font);
            if (width > box.Width - 2)
            {
                size *= (box.Width - 2) / width;
            }

            if (size < 4)
            {
                continue;
            }

            font = new XFont(FontFamilies.NotoSans, size, XFontStyleEx.Regular);
            var form = StampForms.Draw(page.Owner, new PdfSize(box.Width, box.Height), 0, graphics =>
                graphics.DrawString(label, font, brush, new XRect(0, 0, box.Width, box.Height), XStringFormats.Center));
            placements.Add(new FormPlacement(form.Form, form.FormToLocal.Then(PdfMatrix.Translation(box.X, box.Y)).Then(mapper.ScreenToPdfMatrix)));
        }

        PageStamper.Place(page, placements, StampDepth.Above);
    }

    private static double MeasureWidth(string text, XFont font)
    {
        using var scratch = new PdfDocument();
        using var graphics = XGraphics.FromPdfPage(scratch.AddPage());
        return graphics.MeasureString(text, font).Width;
    }

    /// <summary>
    /// Reads the written document with PdfPig: the redacted pages (0-based) showing text they may not — a picture page
    /// any letter, a page that kept its text a letter whose glyph lies (by its centre) under an area.
    /// </summary>
    private static List<int> Leaks(byte[] bytes, IReadOnlyList<RedactedPage> pages, IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas)
    {
        using var pig = PigDocument.Open(bytes);
        var leaks = new List<int>();
        foreach (var redacted in pages)
        {
            var letters = pig.GetPage(redacted.PageIndex + 1).Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value));
            var left = redacted.Raster is not null
                ? letters.Any()
                : letters.Any(l => areas[redacted.PageIndex].Any(r => Inside(l.BoundingBox.Centroid, r)));
            if (left)
            {
                leaks.Add(redacted.PageIndex);
            }
        }

        return leaks;
    }

    /// <summary>On the PDFium thread: the given pages rendered as pictures from the document as it was.</summary>
    private List<RedactedPage> AsPictures(
        string inputPath,
        IReadOnlyList<RedactedPage> pages,
        IReadOnlyList<int> indexes,
        IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas,
        RgbColor fill)
    {
        using var original = Open(inputPath);
        return [.. pages.Select(p => indexes.Contains(p.PageIndex) ? p with { Raster = RedactionRaster.Render(original, p.PageIndex, areas[p.PageIndex], fill) } : p)];
    }

    private static bool Inside(UglyToad.PdfPig.Core.PdfPoint point, PdfRect rect)
        => point.X > rect.Left && point.X < rect.Right && point.Y > rect.Bottom && point.Y < rect.Top;

    private static string N(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}
