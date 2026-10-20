using System.IO;
using System.Printing;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Printing;

/// <summary>Prints through the XPS print path on a background STA thread (see <see cref="XpsPrintRunner"/>).</summary>
internal sealed class DocumentPrinter(IPageImageRenderer renderer, ILogger<DocumentPrinter> logger) : IDocumentPrinter
{
    public async Task<PrintOutcome> PrintAsync(VirtualDocument document, PrintJob job, string jobName, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(job);

        PdfPrintPaginator? paginator = null;
        bool completed;
        try
        {
            completed = await XpsPrintRunner.RunAsync(
                () => OpenQueue(job, jobName),
                token => paginator = new PdfPrintPaginator(document, renderer, job.PageIndices, job.Sheet, logger, token),
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PrintSystemException or PrintingCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new PrintFailedException("The document could not be sent to the printer.", ex);
        }

        var failed = paginator?.FailedPages ?? 0;
        logger.LogInformation(
            "Print job {Outcome}: {Pages} page(s), {Failed} left blank",
            completed ? "completed" : "cancelled",
            job.PageIndices.Count,
            failed);
        return new PrintOutcome(!completed, failed);
    }

    private static XpsTarget OpenQueue(PrintJob job, string jobName)
    {
        var server = string.IsNullOrEmpty(job.PrintServerName) ? new LocalPrintServer() : new PrintServer(job.PrintServerName);
        try
        {
            var queue = server.GetPrintQueue(job.QueueName);
            queue.CurrentJobSettings.Description = jobName;
            var ticket = new PrintTicket(new MemoryStream(job.TicketXml, writable: false));
            return new XpsTarget(PrintQueue.CreateXpsDocumentWriter(queue), ticket, new Resources(queue, server));
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private sealed class Resources(PrintQueue queue, PrintServer server) : IDisposable
    {
        public void Dispose()
        {
            queue.Dispose();
            server.Dispose();
        }
    }
}
