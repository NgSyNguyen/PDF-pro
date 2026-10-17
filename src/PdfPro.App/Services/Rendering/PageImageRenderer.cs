using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>A rendered page image ready for WPF: frozen, so it can cross threads.</summary>
public sealed record RenderedPageImage(ImageSource Image, long ByteSize, PixelRect Region, int PagePixelWidth, int PagePixelHeight);

/// <summary>Renders pages to WPF images (seam for view-model tests).</summary>
public interface IPageImageRenderer
{
    /// <param name="scale">Device pixels per PDF point.</param>
    /// <param name="region">Part of the full page bitmap to render, or null for the whole page.</param>
    Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// PDFium renders on its own thread; the pixels are copied into a frozen <see cref="BitmapSource"/> on a pool
/// thread, so the UI thread only swaps the image in.
/// </summary>
internal sealed class PageImageRenderer : IPageImageRenderer
{
    public async Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var options = new PageRenderOptions { Scale = scale, Region = region, Flags = PdfRenderFlags.Annotations };
        using var bitmap = await document.RenderPageAsync(pageIndex, options, priority, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var image = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            bitmap.Scan0,
            checked(bitmap.Stride * bitmap.Height),
            bitmap.Stride);
        image.Freeze();
        return new RenderedPageImage(image, bitmap.ByteSize, bitmap.Region, bitmap.PagePixelWidth, bitmap.PagePixelHeight);
    }
}
