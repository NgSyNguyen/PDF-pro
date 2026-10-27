using System.IO;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Editing;

/// <summary>Shows what a stamp does to a page: the page rendered after the operation ran on it.</summary>
public interface IStampPreview : IAsyncDisposable
{
    /// <summary>
    /// The page with <paramref name="operation"/> applied (no operation: the page as it is), about
    /// <paramref name="width"/> pixels wide; null when it cannot be shown.
    /// </summary>
    Task<ImageSource?> RenderAsync(IContentOperation? operation, object? parameters, double width, CancellationToken cancellationToken);
}

/// <summary>
/// The real thing, not an imitation: one page of the document (as its plan shows it) is written once to a small PDF in
/// the session's work folder; each preview runs the stamp operation on that file and PDFium renders the result. Every
/// file it makes is removed when the dialog closes.
/// </summary>
public sealed class StampPreview(
    DocumentWorkspace workspace,
    int pageIndex,
    IDocumentSaver saver,
    IPdfDocumentLoader loader,
    IPageImageRenderer renderer,
    ILogger logger) : IStampPreview
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _page;

    public async Task<ImageSource?> RenderAsync(IContentOperation? operation, object? parameters, double width, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var output = Path.Combine(workspace.WorkDirectory, $"preview-{Guid.NewGuid():N}.pdf");
        try
        {
            var page = await PageAsync(cancellationToken);
            var shown = page;
            if (operation is not null && parameters is not null)
            {
                var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (stream.ConfigureAwait(false))
                {
                    await operation.ApplyToFileAsync(page, stream, parameters, new Progress<OperationProgress>(), cancellationToken);
                }

                shown = output;
            }

            var document = await loader.LoadAsync(shown, null, cancellationToken);
            await using (document.ConfigureAwait(false))
            {
                var size = document.PageSizes[0];
                var image = await renderer.RenderAsync(document, 0, new PageRenderOptions { Scale = width / Math.Max(1, size.Width), Flags = PdfRenderFlags.None }, PdfiumPriority.High, cancellationToken);
                return image.Image;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PdfDocumentException or OperationValidationException or UnauthorizedAccessException)
        {
            logger.LogWarning("A stamp preview could not be made: {ErrorType}", ex.GetType().Name);
            return null;
        }
        finally
        {
            TryDelete(output);
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_page is not null)
            {
                TryDelete(_page);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<string> PageAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            Directory.CreateDirectory(workspace.WorkDirectory);
            var path = Path.Combine(workspace.WorkDirectory, $"preview-page-{Guid.NewGuid():N}.pdf");
            await saver.ExportAsync(workspace, [pageIndex], path, new Progress<OperationProgress>(), cancellationToken);
            _page = path;
        }

        return _page;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preview file still open is left in the work folder, which the session removes.
        }
    }
}
