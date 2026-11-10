using System.Globalization;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Ocr;

/// <summary>
/// Lays the text Tesseract recognised over pages. Its one-page PDF holding only invisible text is turned into a form
/// XObject (<see cref="PageForms"/>) and drawn over the page through a matrix that takes it from the picture OCR read
/// back to the page — undoing the turn and straightening the picture had, then the page's display (visible box and
/// /Rotate, <see cref="PageCoordinateMapper"/>) — so each word lies over its place in the scan. The page's own content,
/// images included, is not touched. Tesseract's glyph-less font is the same in every page it writes, so the
/// document gets it once. Form and content stream are marked <see cref="InvisibleText.MarkerKey"/>.
/// </summary>
internal sealed class OcrTextLayers(PdfDocument document)
{
    private (PdfReference Reference, byte[] Program)? _font;

    /// <summary>Draws a text layer over a page.</summary>
    /// <param name="page">The page.</param>
    /// <param name="textLayer">Tesseract's text-only PDF of the picture.</param>
    /// <param name="pictureToDisplay">From the picture OCR read (pixels, y down) to the page as displayed (points, y down).</param>
    /// <param name="pictureWidth">The width of the picture OCR read, in pixels.</param>
    /// <param name="pictureHeight">Its height, in pixels.</param>
    /// <exception cref="InvalidDataException">The text layer is not a PDF with one page.</exception>
    public void Add(PdfPage page, byte[] textLayer, PdfMatrix pictureToDisplay, int pictureWidth, int pictureHeight)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(textLayer);
        PdfDocument source;
        try
        {
            source = PdfReader.Open(new MemoryStream(textLayer, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException)
        {
            throw new InvalidDataException("The OCR text layer is not a PDF.", ex);
        }

        using (source)
        {
            if (source.PageCount != 1)
            {
                throw new InvalidDataException("The OCR text layer has no page.");
            }

            var form = PageForms.Import(document, source, 0);
            var formDictionary = AsDictionary(form.Form)!;
            formDictionary.Elements.SetBoolean(InvisibleText.MarkerKey, true);
            ShareFont(formDictionary);

            // Form space (points, y up, the picture's size) → picture pixels (y down).
            var toPicture = new PdfMatrix(pictureWidth / form.Size.Width, 0, 0, -pictureHeight / form.Size.Height, 0, pictureHeight);
            var toPage = new PageCoordinateMapper(PageStamper.Geometry(page), 1).ScreenToPdfMatrix;
            PageStamper.Place(page, [new FormPlacement(form.Form, toPicture.Then(pictureToDisplay).Then(toPage))], StampDepth.Above, markerKey: InvisibleText.MarkerKey);
        }
    }

    /// <summary>
    /// Turns everything drawn on the page clockwise by <paramref name="degrees"/> around the middle of its visible box,
    /// as the page is displayed (a scan whose lines rise to the right by that angle comes out level).
    /// </summary>
    public void Straighten(PdfPage page, double degrees)
    {
        ArgumentNullException.ThrowIfNull(page);
        var geometry = PageStamper.Geometry(page);
        var box = geometry.Box;
        var (cx, cy) = ((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);

        // Clockwise on screen is clockwise in the page's own space too (a turn keeps its sense under /Rotate).
        var m = PdfMatrix.Translation(-cx, -cy).Then(PdfMatrix.Rotation(-degrees)).Then(PdfMatrix.Translation(cx, cy));
        var contents = new PdfArray(document);
        contents.Elements.Add(NewStream(string.Create(CultureInfo.InvariantCulture, $"q {N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm\n")));
        PageStamper.ContentStreams(page).ForEach(contents.Elements.Add);
        contents.Elements.Add(NewStream("\nQ\n"));
        page.Elements["/Contents"] = contents;
    }

    /// <summary>Points the form's fonts at the document's copy of Tesseract's glyph-less font when it is the same font.</summary>
    private void ShareFont(PdfDictionary form)
    {
        if (AsDictionary(AsDictionary(form.Elements["/Resources"])?.Elements["/Font"]) is not { } fonts)
        {
            return;
        }

        foreach (var key in fonts.Elements.Keys.ToList())
        {
            if (fonts.Elements[key] is not PdfReference reference || AsDictionary(reference) is not { } font
                || Name(font, "/BaseFont") != "/GlyphLessFont" || Program(font) is not { } program)
            {
                continue;
            }

            if (_font is { } shared && shared.Program.AsSpan().SequenceEqual(program))
            {
                fonts.Elements[key] = shared.Reference;
            }
            else
            {
                _font ??= (reference, program);
            }
        }
    }

    /// <summary>The bytes of the TrueType program of a Type 0 font (as stored), or null.</summary>
    private static byte[]? Program(PdfDictionary font)
    {
        var descendant = AsDictionary(AsArray(font.Elements["/DescendantFonts"])?.Elements.FirstOrDefault());
        var descriptor = AsDictionary(descendant?.Elements["/FontDescriptor"]);
        return AsDictionary(descriptor?.Elements["/FontFile2"])?.Stream?.Value;
    }

    private PdfReference NewStream(string text)
    {
        var content = new PdfContent(document);
        content.CreateStream(System.Text.Encoding.ASCII.GetBytes(text));
        document.Internals.AddObject(content);
        return content.Reference!;
    }

    private static string N(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
