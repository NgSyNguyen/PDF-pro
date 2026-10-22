using System.Globalization;
using System.IO.Compression;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Layout;

/// <summary>A source page turned into a form XObject of the output: drawn at the origin with its displayed size.</summary>
/// <param name="Form">Reference to the form XObject in the output document.</param>
/// <param name="Size">The page as displayed (its /Rotate applied, cropped to its visible box), in points.</param>
internal readonly record struct PageForm(PdfReference Form, PdfSize Size);

/// <summary>
/// Turns pages into form XObjects so several can be drawn on one sheet (N-up, booklets, posters) with their own
/// resources. PDFsharp's XPdfForm ignores the CropBox, so the form is built here: its /BBox is the visible box (as
/// PDFium computes it) and its /Matrix applies the page's /Rotate, so it shows exactly what a viewer shows. Content
/// streams are joined, not re-interpreted: text stays text. Annotations (links, notes) do not come along.
/// </summary>
internal static class PageForms
{
    /// <exception cref="MaterializationException">A content stream uses a filter PDFsharp cannot decode.</exception>
    public static PageForm Import(PdfDocument output, PdfDocument source, int pageIndex)
    {
        var sourcePage = source.Pages[pageIndex];

        // Annotations would drag every page they link to into the import; they are not drawn on sheets anyway.
        sourcePage.Elements.Remove("/Annots");
        sourcePage.Elements.Remove("/B");
        var imported = output.AddPage(sourcePage);
        output.Pages.Remove(imported);

        var visible = PageBoxes.VisibleBox(imported);
        var rotation = PageBoxes.Rotation(imported);
        var form = new PdfDictionary(output);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Numbers(output, visible.Left, visible.Bottom, visible.Right, visible.Top);
        form.Elements["/Matrix"] = Numbers(output, Matrix(rotation, visible));
        if (imported.Elements["/Resources"] is { } resources)
        {
            form.Elements["/Resources"] = resources;
        }

        if (imported.Elements["/Group"] is { } group)
        {
            form.Elements["/Group"] = group;
        }

        form.CreateStream(Compress(JoinedContent(imported)));
        form.Elements.SetName("/Filter", "/FlateDecode");
        output.Internals.AddObject(form);
        var size = rotation % 180 == 0 ? visible.Size : new PdfSize(visible.Height, visible.Width);
        return new PageForm(form.Reference!, size);
    }

    /// <summary>
    /// Maps the visible box to (0, 0)–(width, height) as displayed: the page turned clockwise by its /Rotate.
    /// </summary>
    internal static double[] Matrix(int rotation, PdfRect visible) => rotation switch
    {
        90 => [0, -1, 1, 0, -visible.Bottom, visible.Right],
        180 => [-1, 0, 0, -1, visible.Right, visible.Top],
        270 => [0, 1, -1, 0, visible.Top, -visible.Left],
        _ => [1, 0, 0, 1, -visible.Left, -visible.Bottom],
    };

    private static byte[] JoinedContent(PdfPage page)
    {
        var streams = new List<PdfDictionary>();
        switch (Resolve(page.Elements["/Contents"]))
        {
            case PdfArray array:
                streams.AddRange(array.Elements.Select(AsDictionary).OfType<PdfDictionary>());
                break;
            case PdfDictionary single:
                streams.Add(single);
                break;
        }

        using var joined = new MemoryStream();
        foreach (var stream in streams)
        {
            byte[] bytes;
            try
            {
                bytes = stream.Stream?.UnfilteredValue ?? [];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new MaterializationException(MaterializationFailure.SourceUnreadable, SourceIds.Original, ex);
            }

            joined.Write(bytes);
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

    private static PdfArray Numbers(PdfDocument document, params double[] values)
    {
        var array = new PdfArray(document);
        foreach (var value in values)
        {
            array.Elements.Add(new PdfReal(Math.Round(value, 4)));
        }

        return array;
    }

    /// <summary>Formats a number for a content stream.</summary>
    internal static string Number(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
