using PdfPro.Core.Geometry;
using PdfPro.Operations.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Merging;

/// <summary>One line of the table of contents.</summary>
/// <param name="Title">The file's title.</param>
/// <param name="PageNumber">1-based page the file starts on.</param>
internal readonly record struct TableOfContentsEntry(string Title, int PageNumber);

/// <summary>
/// Draws the table of contents of a merged document on its reserved blank pages: a heading, then one line per file
/// with dot leaders and the page number, the whole line a link to that page. Text uses the bundled Noto Sans, so
/// Vietnamese titles keep their marks.
/// </summary>
internal static class TableOfContentsWriter
{
    private const double SideMargin = 56;
    private const double TopMargin = 64;
    private const double BottomMargin = 56;
    private const double HeadingHeight = 44;
    private const double LineHeight = 20;
    private const double HeadingSize = 18;
    private const double EntrySize = 11;

    /// <summary>How many lines fit on a page of <paramref name="pageSize"/> (at least one).</summary>
    public static int EntriesPerPage(PdfSize pageSize)
        => Math.Max(1, (int)Math.Floor((pageSize.Height - TopMargin - HeadingHeight - BottomMargin) / LineHeight));

    public static void Draw(IReadOnlyList<PdfPage> pages, string heading, IReadOnlyList<TableOfContentsEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(entries);
        BundledFonts.Register();
        var headingFont = new XFont(BundledFonts.Family, HeadingSize, XFontStyleEx.Bold);
        var entryFont = new XFont(BundledFonts.Family, EntrySize, XFontStyleEx.Regular);
        var next = 0;
        for (var p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            var size = new PdfSize(page.Width.Point, page.Height.Point);
            var perPage = EntriesPerPage(size);
            var mapper = new PageCoordinateMapper(new PdfRect(0, 0, size.Width, size.Height), 0, 1);
            using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            if (p == 0)
            {
                graphics.DrawString(heading, headingFont, XBrushes.Black, new XRect(SideMargin, TopMargin, size.Width - (2 * SideMargin), HeadingHeight), XStringFormats.TopCenter);
            }

            var y = TopMargin + HeadingHeight;
            for (var line = 0; line < perPage && next < entries.Count; line++, next++)
            {
                var entry = entries[next];
                var row = new ViewRect(SideMargin, y, size.Width - (2 * SideMargin), LineHeight);
                DrawEntry(graphics, entryFont, row, entry);
                var link = mapper.ScreenRectToPdf(row);
                page.AddDocumentLink(new PdfRectangle(new XPoint(link.Left, link.Bottom), new XPoint(link.Right, link.Top)), entry.PageNumber);
                y += LineHeight;
            }
        }
    }

    private static void DrawEntry(XGraphics graphics, XFont font, ViewRect row, TableOfContentsEntry entry)
    {
        var number = entry.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var numberWidth = graphics.MeasureString(number, font).Width;
        var titleRoom = row.Width - numberWidth - 24;
        var title = Fit(graphics, font, entry.Title, titleRoom);
        var titleWidth = graphics.MeasureString(title, font).Width;
        var baseline = new XRect(row.X, row.Y, row.Width, row.Height);
        graphics.DrawString(title, font, XBrushes.Black, baseline, XStringFormats.CenterLeft);
        graphics.DrawString(number, font, XBrushes.Black, baseline, XStringFormats.CenterRight);

        // Dot leaders between the title and the number.
        var dotWidth = graphics.MeasureString(" .", font).Width;
        var room = row.Width - titleWidth - numberWidth - 12;
        if (room > dotWidth)
        {
            var dots = string.Concat(Enumerable.Repeat(" .", (int)(room / dotWidth)));
            var dotsWidth = graphics.MeasureString(dots, font).Width;
            graphics.DrawString(dots, font, XBrushes.Gray, new XRect(row.X + row.Width - numberWidth - 6 - dotsWidth, row.Y, dotsWidth, row.Height), XStringFormats.CenterLeft);
        }
    }

    /// <summary>The title, shortened with "…" when it is wider than <paramref name="room"/>.</summary>
    private static string Fit(XGraphics graphics, XFont font, string title, double room)
    {
        if (graphics.MeasureString(title, font).Width <= room)
        {
            return title;
        }

        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(title);
        for (var count = elements.Length - 1; count > 0; count--)
        {
            var candidate = title[..elements[count]].TrimEnd() + "…";
            if (graphics.MeasureString(candidate, font).Width <= room)
            {
                return candidate;
            }
        }

        return "…";
    }
}
