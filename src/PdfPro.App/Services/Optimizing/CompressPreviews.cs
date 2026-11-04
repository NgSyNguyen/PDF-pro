using System.IO;
using System.Windows.Media;
using PdfPro.App.Services.Rendering;
using PdfPro.Operations.Optimizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Optimizing;

/// <summary>One page before and after compression, rendered by PDFium, with what compressing it did.</summary>
public sealed record CompressPreviewImages(ImageSource Before, ImageSource After, CompressReport Report);

/// <summary>Shows what a compression preset does to the pictures of a page before the whole file is compressed.</summary>
public interface ICompressPreviews
{
    /// <summary>
    /// Page <paramref name="pageIndex"/> (0-based) of <paramref name="input"/> alone, before and after compression with
    /// <paramref name="parameters"/>, rendered at about <paramref name="scale"/> pixels per point. The one-page files are
    /// written to <paramref name="workFolder"/> (the document's session folder, never a shared temporary folder) and
    /// removed.
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    /// <exception cref="PdfDocumentException">PDFium cannot open a compressed page.</exception>
    Task<CompressPreviewImages> RenderAsync(string input, int pageIndex, CompressParams parameters, double scale, string workFolder, CancellationToken cancellationToken);
}

/// <summary>The real thing: <see cref="CompressOperation.PreviewAsync"/>, then both pages rendered by PDFium.</summary>
public sealed class CompressPreviews(CompressOperation operation, IPdfDocumentLoader loader, IPageImageRenderer renderer) : ICompressPreviews
{
    /// <summary>Pixels of a rendered page at most (a poster is rendered smaller than asked).</summary>
    public const double MaxPixels = 16_000_000;

    public async Task<CompressPreviewImages> RenderAsync(string input, int pageIndex, CompressParams parameters, double scale, string workFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        var preview = await operation.PreviewAsync(input, pageIndex, parameters, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(workFolder);
        var before = await RenderAsync(preview.Before, workFolder, scale, cancellationToken).ConfigureAwait(false);
        var after = await RenderAsync(preview.After, workFolder, scale, cancellationToken).ConfigureAwait(false);
        return new CompressPreviewImages(before, after, preview.Report);
    }

    private async Task<ImageSource> RenderAsync(byte[] file, string workFolder, double scale, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workFolder, $"compress-preview-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(path, file, cancellationToken).ConfigureAwait(false);
            var document = await loader.LoadAsync(path, password: null, cancellationToken).ConfigureAwait(false);
            await using (document.ConfigureAwait(false))
            {
                var size = document.PageSizes[0];
                var fitting = Math.Sqrt(MaxPixels / Math.Max(1, size.Width * size.Height));
                var options = new PageRenderOptions { Scale = Math.Min(scale, fitting), Flags = PdfRenderFlags.Annotations };
                var rendered = await renderer.RenderAsync(document, 0, options, PdfiumPriority.High, cancellationToken).ConfigureAwait(false);
                return rendered.Image;
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A page still open is left in the session folder, which is removed with the session.
        }
    }
}
