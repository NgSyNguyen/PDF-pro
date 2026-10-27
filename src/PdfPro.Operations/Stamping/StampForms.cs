using System.IO.Compression;
using PdfPro.Core.Geometry;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// A drawing turned into a form XObject: <see cref="Form"/> holds it, <see cref="FormToLocal"/> maps the form's space
/// to the drawing's own space (origin top-left, y down, points) so that "FormToLocal, then local to page" is the "cm"
/// that places it.
/// </summary>
internal readonly record struct StampForm(PdfReference Form, PdfMatrix FormToLocal);

/// <summary>
/// Draws with PDFsharp's <see cref="XGraphics"/> into form XObjects, using only public API: the drawing is made on a
/// scratch page of the right size (no /Rotate, origin at 0,0 — where XGraphics is well defined), whose content and
/// resources then become the form, and the scratch page is removed. A margin around the drawing keeps strokes, arrow
/// heads and slanted letters that reach past its box from being cut off by the form's bounding box.
/// </summary>
internal static class StampForms
{
    /// <param name="document">The document the form belongs to (opened for modification).</param>
    /// <param name="size">Size of the drawing's own space, in points.</param>
    /// <param name="margin">Extra room on every side, in points.</param>
    /// <param name="draw">Paints at (0, 0)–(width, height), y down.</param>
    public static StampForm Draw(PdfDocument document, PdfSize size, double margin, Action<XGraphics> draw)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        var (width, height) = (size.Width + (2 * margin), size.Height + (2 * margin));

        var scratch = document.AddPage();
        try
        {
            scratch.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
            using (var graphics = XGraphics.FromPdfPage(scratch, XGraphicsPdfPageOptions.Replace))
            {
                graphics.TranslateTransform(margin, margin);
                draw(graphics);
            }

            var form = new PdfDictionary(document);
            form.Elements.SetName("/Type", "/XObject");
            form.Elements.SetName("/Subtype", "/Form");
            var box = new PdfArray(document);
            foreach (var value in new[] { 0, 0, width, height })
            {
                box.Elements.Add(new PdfReal(Math.Round(value, 4)));
            }

            form.Elements["/BBox"] = box;
            if (scratch.Elements["/Resources"] is { } resources)
            {
                form.Elements["/Resources"] = resources;
            }

            form.CreateStream(Compress(ContentOf(scratch)));
            form.Elements.SetName("/Filter", "/FlateDecode");
            document.Internals.AddObject(form);

            // Form space is y up over (0,0)–(width,height); the drawing's space is y down, shifted by the margin.
            return new StampForm(form.Reference!, new PdfMatrix(1, 0, 0, -1, -margin, height - margin));
        }
        finally
        {
            document.Pages.Remove(scratch);
        }
    }

    private static byte[] ContentOf(PdfPage page)
    {
        using var joined = new MemoryStream();
        var streams = Resolve(page.Elements["/Contents"]) switch
        {
            PdfArray array => array.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToArray(),
            PdfDictionary single => [single],
            _ => [],
        };
        foreach (var stream in streams)
        {
            joined.Write(stream.Stream?.UnfilteredValue ?? []);
            joined.WriteByte((byte)'\n');
        }

        return joined.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
