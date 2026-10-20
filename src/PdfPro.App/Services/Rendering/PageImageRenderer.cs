using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Services.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>A rendered page image ready for WPF: frozen, so it can cross threads.</summary>
public sealed record RenderedPageImage(ImageSource Image, long ByteSize, PixelRect Region, int PagePixelWidth, int PagePixelHeight);

/// <summary>Renders pages to WPF images (seam for view-model tests).</summary>
public interface IPageImageRenderer
{
    /// <param name="options">Scale in device pixels per PDF point, region, flags (screen or print) and extra rotation.</param>
    Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, PageRenderOptions options, PdfiumPriority priority, CancellationToken cancellationToken);
}

public static class PageImageRendererExtensions
{
    /// <summary>Screen rendering: annotations drawn, the page as it is rotated in the file.</summary>
    /// <param name="scale">Device pixels per PDF point.</param>
    /// <param name="region">Part of the full page bitmap to render, or null for the whole page.</param>
    public static Task<RenderedPageImage> RenderAsync(this IPageImageRenderer renderer, IPdfDocument document, int pageIndex, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var options = new PageRenderOptions { Scale = scale, Region = region, Flags = PdfRenderFlags.Annotations };
        return renderer.RenderAsync(document, pageIndex, options, priority, cancellationToken);
    }

    /// <summary>Renders a page of the virtual document as displayed (the plan's rotation and crop applied).</summary>
    /// <param name="renderer">The renderer.</param>
    /// <param name="page">The page; blank pages are not rendered.</param>
    /// <param name="scale">Device pixels per PDF point.</param>
    /// <param name="region">Part of the displayed page in pixels, or null for the whole page.</param>
    /// <param name="flags">Annotations (screen) or printing flags.</param>
    /// <param name="priority">Queue priority on the PDFium thread.</param>
    /// <param name="cancellationToken">Cancels before the render starts.</param>
    public static Task<RenderedPageImage> RenderAsync(this IPageImageRenderer renderer, VirtualPage page, double scale, PixelRect? region, PdfRenderFlags flags, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(page);
        var source = page.Source ?? throw new ArgumentException("Blank pages are not rendered.", nameof(page));
        return renderer.RenderAsync(source, page.SourcePageIndex, page.RenderOptions(scale, region, flags), priority, cancellationToken);
    }
}

/// <summary>
/// PDFium renders on its own thread; the pixels are copied into a frozen <see cref="BitmapSource"/> on a pool
/// thread, so the UI thread only swaps the image in.
/// </summary>
internal sealed class PageImageRenderer : IPageImageRenderer
{
    public async Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, PageRenderOptions options, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
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
