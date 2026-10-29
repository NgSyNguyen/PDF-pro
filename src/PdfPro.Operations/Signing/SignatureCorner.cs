using PdfPro.Core.Geometry;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Signing;

/// <summary>Which page a signature placed in a corner goes on (when the same placement serves many files).</summary>
public enum SignaturePage
{
    First,
    Last,
}

/// <summary>
/// Where a visible signature goes when files are signed in a batch: the bottom right corner of their first or last
/// page as it is shown (turned pages included), the same size and margin on every file.
/// </summary>
public static class SignatureCorner
{
    /// <summary>The box of a signature, as it shows (points).</summary>
    public static readonly PdfSize DefaultSize = new(180, 60);

    /// <summary>Its distance from the page's edges, as it shows (points).</summary>
    public const double Margin = 36;

    /// <summary>The page (1-based) and the box in its PDF space for a signature in the bottom right corner.</summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static (int Page, PdfRect Rect) Place(byte[] file, SignaturePage page)
    {
        ArgumentNullException.ThrowIfNull(file);
        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The file cannot be read.", ex);
        }

        using (document)
        {
            if (document.PageCount == 0)
            {
                throw new InvalidDataException("The file has no page.");
            }

            var index = page == SignaturePage.First ? 0 : document.PageCount - 1;
            var target = document.Pages[index];
            return (index + 1, Place(new PageGeometry(PageBoxes.VisibleBox(target), PageBoxes.Rotation(target))));
        }
    }

    /// <summary>The bottom right corner of a page as it is shown, in its PDF space (smaller on small pages).</summary>
    public static PdfRect Place(PageGeometry geometry)
    {
        var shown = geometry.DisplaySize;
        var margin = Math.Min(Margin, Math.Min(shown.Width, shown.Height) / 10);
        var width = Math.Min(DefaultSize.Width, shown.Width - (2 * margin));
        var height = Math.Min(DefaultSize.Height, shown.Height - (2 * margin));
        var mapper = new PageCoordinateMapper(geometry, 1);
        return mapper.ScreenRectToPdf(new ViewRect(shown.Width - margin - width, shown.Height - margin - height, width, height));
    }
}
