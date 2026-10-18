using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Printing;

/// <summary>The sheet chosen in the print dialog, in device-independent units (1/96 inch), and the printer resolution.</summary>
/// <param name="Paper">Sheet size as oriented in the print ticket.</param>
/// <param name="PrintableArea">Part of the sheet the printer can print on.</param>
/// <param name="Dpi">Printer resolution in dots per inch.</param>
public sealed record PrintSheet(ViewSize Paper, ViewRect PrintableArea, double Dpi);

/// <summary>
/// A print job chosen in the print dialog. Print queues belong to the thread that opened them, so the queue is
/// identified by name and the ticket kept as XML; both are opened again on the print thread.
/// </summary>
public sealed record PrintJob(string PrintServerName, string QueueName, byte[] TicketXml, PrintSheet Sheet, IReadOnlyList<int> PageIndices);

/// <summary>How a print job ended.</summary>
/// <param name="Cancelled">The user cancelled; part of the job may already have reached the printer.</param>
/// <param name="FailedPages">Pages that could not be rendered and were printed blank.</param>
public sealed record PrintOutcome(bool Cancelled, int FailedPages);

/// <summary>Shows the system print dialog (seam for view-model tests).</summary>
public interface IPrintDialogService
{
    /// <summary>The job the user set up, or null when the dialog was cancelled.</summary>
    /// <param name="currentPageIndex">Printed when the user picks "Current page".</param>
    PrintJob? ShowPrintDialog(int pageCount, int currentPageIndex);
}

/// <summary>Sends pages to a printer in the background (seam for view-model tests).</summary>
public interface IDocumentPrinter
{
    /// <param name="jobName">Name shown in the printer queue.</param>
    /// <param name="progress">Number of pages handed to the printer so far.</param>
    /// <exception cref="PrintFailedException">The printer or spooler refused the job.</exception>
    Task<PrintOutcome> PrintAsync(IPdfDocument document, PrintJob job, string jobName, IProgress<int>? progress, CancellationToken cancellationToken);
}

/// <summary>The job could not be sent to the printer (offline printer, spooler error, invalid ticket…).</summary>
public sealed class PrintFailedException : Exception
{
    public PrintFailedException()
    {
    }

    public PrintFailedException(string message)
        : base(message)
    {
    }

    public PrintFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
