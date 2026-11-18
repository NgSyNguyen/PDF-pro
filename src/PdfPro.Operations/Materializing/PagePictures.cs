using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// A picture of a page as it is shown (its /Rotate applied, covering its visible box): 8-bit grey or RGB samples
/// encoded with <paramref name="Filter"/> ("/FlateDecode" or "/DCTDecode"); row 0 is the top of the page as shown.
/// </summary>
internal sealed record PagePicture(int Width, int Height, bool IsGray, byte[] Data, string Filter);

/// <summary>Replacing what a page draws with a picture of it (safe redaction, pages that could not be made grey).</summary>
internal static class PagePictures
{
    /// <summary>
    /// The page draws <paramref name="picture"/> (named <paramref name="imageName"/>) over its visible box, then
    /// <paramref name="drawnAbove"/> (content in PDF space, or null); its old content, resources, thumbnail and private
    /// data are gone. Annotations stay.
    /// </summary>
    public static void Replace(PdfDocument document, PdfPage page, PagePicture picture, string imageName, string? drawnAbove = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(picture);
        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "/XObject");
        image.Elements.SetName("/Subtype", "/Image");
        image.Elements.SetInteger("/Width", picture.Width);
        image.Elements.SetInteger("/Height", picture.Height);
        image.Elements.SetName("/ColorSpace", picture.IsGray ? "/DeviceGray" : "/DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.CreateStream(picture.Data);
        image.Elements.SetName("/Filter", picture.Filter);
        document.Internals.AddObject(image);

        // The picture's unit square goes to the shown page, then to PDF space.
        var geometry = new PageGeometry(PageBoxes.VisibleBox(page), PageBoxes.Rotation(page));
        var shown = geometry.DisplaySize;
        var m = new PdfMatrix(shown.Width, 0, 0, -shown.Height, 0, shown.Height).Then(new PageCoordinateMapper(geometry, 1).ScreenToPdfMatrix);
        var content = new StringBuilder();
        content.Append(CultureInfo.InvariantCulture, $"q {N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm {imageName} Do Q\n");
        if (drawnAbove is not null)
        {
            content.Append(drawnAbove);
        }

        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        document.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference!;

        var xObjects = new PdfDictionary(document);
        xObjects.Elements[imageName] = image.Reference!;
        var resources = new PdfDictionary(document);
        resources.Elements["/XObject"] = xObjects;
        page.Elements["/Resources"] = resources;

        // Old pictures of the page, private data of the program that made it and page-level metadata could show the content.
        foreach (var key in new[] { "/Thumb", "/PieceInfo", "/Metadata", "/StructParents", "/B", "/Group" })
        {
            page.Elements.Remove(key);
        }
    }

    /// <summary>A number for content streams: up to five decimals, invariant.</summary>
    public static string N(double value) => Math.Round(value, 5).ToString("0.#####", CultureInfo.InvariantCulture);
}
