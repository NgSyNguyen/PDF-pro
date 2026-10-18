using System.Printing;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Geometry;
using PdfPro.Core.Printing;

namespace PdfPro.App.Services.Printing;

/// <summary>The Windows print dialog: printer, copies, orientation, page range (all, current page or from–to).</summary>
internal sealed class PrintDialogService(ILogger<PrintDialogService> logger) : IPrintDialogService
{
    /// <summary>ISO A4 in device-independent units, when the driver reports no paper size.</summary>
    private static readonly ViewSize FallbackPaper = new(793.7, 1122.5);

    private const double FallbackDpi = 300;

    public PrintJob? ShowPrintDialog(int pageCount, int currentPageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)pageCount,
            PageRange = new PageRange(1, pageCount),
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [Math.Clamp(currentPageIndex, 0, pageCount - 1)],
            PageRangeSelection.UserPages => PrintPageRange.Between(dialog.PageRange.PageFrom, dialog.PageRange.PageTo, pageCount),
            _ => PrintPageRange.All(pageCount),
        };

        var queue = dialog.PrintQueue;
        var ticket = dialog.PrintTicket;
        return new PrintJob(
            queue.HostingPrintServer?.Name ?? string.Empty,
            queue.Name,
            ticket.GetXmlStream().ToArray(),
            DescribeSheet(queue, ticket, logger),
            pages);
    }

    /// <summary>Paper and printable area as oriented by the ticket (the driver's capabilities, else the ticket).</summary>
    private static PrintSheet DescribeSheet(PrintQueue queue, PrintTicket ticket, ILogger logger)
    {
        double? width = null;
        double? height = null;
        PageImageableArea? imageable = null;
        try
        {
            var capabilities = queue.GetPrintCapabilities(ticket);
            width = capabilities.OrientedPageMediaWidth;
            height = capabilities.OrientedPageMediaHeight;
            imageable = capabilities.PageImageableArea;
        }
        catch (PrintQueueException ex)
        {
            logger.LogWarning(ex, "The printer capabilities could not be read; using the ticket's paper size");
        }

        if (width is not > 0 || height is not > 0)
        {
            width = ticket.PageMediaSize?.Width;
            height = ticket.PageMediaSize?.Height;
            if (ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape && width < height)
            {
                (width, height) = (height, width);
            }
        }

        var paper = width is > 0 && height is > 0 ? new ViewSize(width.Value, height.Value) : FallbackPaper;
        var dpi = ticket.PageResolution?.X is int x and > 0 ? x : FallbackDpi;
        var reported = imageable is null ? (ViewRect?)null : new ViewRect(imageable.OriginWidth, imageable.OriginHeight, imageable.ExtentWidth, imageable.ExtentHeight);
        return new PrintSheet(paper, PrintableArea(paper, reported), dpi);
    }

    /// <summary>The imageable area clipped to the sheet; the whole sheet when the driver reports none that fits.</summary>
    internal static ViewRect PrintableArea(ViewSize paper, ViewRect? imageable)
    {
        var sheet = new ViewRect(0, 0, paper.Width, paper.Height);
        if (imageable is not { IsEmpty: false } reported)
        {
            return sheet;
        }

        var area = ViewRect.Intersect(sheet, reported);
        return area.IsEmpty ? sheet : area;
    }
}
