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

/// <param name="Areas">What to remove.</param>
/// <param name="Fill">The colour of the boxes left in place of the removed content (black by default).</param>
/// <param name="Label">Words written in every box ("Đã xóa theo quy định"), or null.</param>
public sealed record RedactParams([property: Required] IReadOnlyList<RedactionArea> Areas, RgbColor? Fill = null, string? Label = null);

/// <summary>What a redaction did, for the user: pages turned into pictures, and what else went with the areas.</summary>
/// <param name="Areas">Rectangles removed.</param>
/// <param name="Pages">The pages redacted (1-based): each is now a picture of itself without the areas.</param>
/// <param name="Annotations">Comments and links removed because they were over an area or named what was removed.</param>
/// <param name="Fields">Form fields removed for the same reasons.</param>
/// <param name="Bookmarks">Bookmarks removed because their title named what was removed.</param>
/// <param name="Metadata">Document information entries removed ("Author", "Keywords"…).</param>
/// <param name="Xmp">The XMP metadata was removed.</param>
public sealed record RedactionReport(int Areas, IReadOnlyList<int> Pages, int Annotations, int Fields, int Bookmarks, IReadOnlyList<string> Metadata, bool Xmp);

/// <summary>
/// "security.redact" — true redaction (SPEC P8 B), a content change: every page with an area to remove is rendered by
/// PDFium at 300 DPI without its annotations, the areas are painted over in the picture and the page's content is
/// replaced by that picture with the boxes (and their label) drawn on top — nothing that was under an area can be
/// selected, copied, searched or extracted any more. Annotations, links, form fields, bookmarks and metadata that show
/// what was removed go too. Afterwards an independent extractor (PdfPig) reads the redacted pages: any text other than
/// the labels stops the operation, and nothing is written. The parameters hold only rectangles, never the removed text.
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
        var total = areas.Count + 2;
        progress.Report(new OperationProgress(0, total, Stage));

        // PDFium: what each page shows under its areas, and the page as a picture with them painted over.
        var pages = await gate.RunAsync(
            () =>
            {
                PdfiumDocument document;
                try
                {
                    document = PdfiumDocument.Open(gate, inputPath);
                }
                catch (PdfDocumentException ex)
                {
                    throw new InvalidDataException("The document cannot be read.", ex);
                }

                using (document)
                {
                    var validation = Validate(request, new DocumentInfo(document.PageCount));
                    if (!validation.IsValid)
                    {
                        throw new OperationValidationException(Id, validation.Errors);
                    }

                    var done = 0;
                    var result = new List<RedactedPage>();
                    foreach (var (index, rects) in areas.OrderBy(a => a.Key))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var under = RedactionRaster.TextUnder(document.GetPageTextLayer(index), rects);
                        result.Add(new RedactedPage(index, under, RedactionRaster.Render(document, index, rects, fill)));
                        progress.Report(new OperationProgress(++done, total, Stage));
                    }

                    return result;
                }
            },
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);

        // PDFsharp: the pages become their pictures, then what else showed the removed content goes.
        var (bytes, cleanup) = await Task.Run(() => Write(inputPath, pages, areas, fill, request.Label, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress.Report(new OperationProgress(total - 1, total, Stage));

        // The mandatory check, with an extractor that is not PDFium: no text but the labels on a redacted page.
        await Task.Run(() => Check(bytes, pages, request.Label), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        Reports.AddOrUpdate(request, new RedactionReport(
            areas.Values.Sum(r => r.Count),
            [.. pages.Select(p => p.PageIndex + 1)],
            cleanup.Annotations,
            cleanup.Fields,
            cleanup.Bookmarks,
            cleanup.Metadata,
            cleanup.Xmp));
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

    private static (byte[] Bytes, CleanupResult Cleanup) Write(
        string inputPath,
        IReadOnlyList<RedactedPage> pages,
        IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> areas,
        RgbColor fill,
        string? label,
        CancellationToken cancellationToken)
    {
        using var document = DocumentContentOperation<RedactParams>.OpenForChange(inputPath);
        foreach (var redacted in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Pages[redacted.PageIndex];
            ReplaceContent(document, page, redacted.Raster!, areas[redacted.PageIndex], fill);
            if (!string.IsNullOrWhiteSpace(label))
            {
                DrawLabels(page, areas[redacted.PageIndex], fill, TextNormalizer.ToNfc(label.Trim()));
            }
        }

        var cleanup = RedactionCleanup.Apply(document, areas, [.. pages.SelectMany(p => p.RemovedTexts)]);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return (stream.ToArray(), cleanup);
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
    /// Reads the written document with PdfPig: a redacted page may only carry the letters of its labels.
    /// </summary>
    /// <exception cref="InvalidOperationException">Text is left on a redacted page; nothing is written.</exception>
    private static void Check(byte[] bytes, IReadOnlyList<RedactedPage> pages, string? label)
    {
        var allowed = string.IsNullOrWhiteSpace(label) ? string.Empty : Letters(TextNormalizer.ToNfc(label.Trim()));
        using var pig = PigDocument.Open(bytes);
        foreach (var redacted in pages)
        {
            var found = Letters(TextNormalizer.ToNfc(string.Concat(pig.GetPage(redacted.PageIndex + 1).Letters.Select(l => l.Value))));
            var rest = allowed.Length == 0 ? found : RemoveLabels(found, allowed);
            if (rest.Length > 0)
            {
                throw new InvalidOperationException($"Text is left on redacted page {redacted.PageIndex + 1}; the redaction was not written.");
            }
        }
    }

    private static string Letters(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>What remains of <paramref name="found"/> once every copy of the label is taken out.</summary>
    private static string RemoveLabels(string found, string label)
    {
        var rest = found;
        int at;
        while ((at = rest.IndexOf(label, StringComparison.Ordinal)) >= 0)
        {
            rest = rest.Remove(at, label.Length);
        }

        return rest;
    }

    private static string N(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}
