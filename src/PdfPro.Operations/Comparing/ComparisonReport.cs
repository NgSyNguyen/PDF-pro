using System.Globalization;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Materializing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Comparing;

/// <summary>The words of a comparison report, in the user's language (the app gives them from its resources).</summary>
/// <param name="Title">"BÁO CÁO SO SÁNH TÀI LIỆU".</param>
/// <param name="OldFile">"Bản cũ: {0} ({1} trang)".</param>
/// <param name="NewFile">"Bản mới: {0} ({1} trang)".</param>
/// <param name="Date">"Ngày so sánh: {0}".</param>
/// <param name="Summary">"{0} thay đổi: {1} thêm, {2} xóa, {3} sửa".</param>
/// <param name="Same">"Hai tài liệu có chữ giống nhau."</param>
/// <param name="Added">"Thêm".</param>
/// <param name="Deleted">"Xóa".</param>
/// <param name="Modified">"Sửa".</param>
/// <param name="Pages">"trang {0} – {1}" (pages numbered from 1; Noto Sans has no arrows).</param>
/// <param name="Visual">"Khác về hình ảnh: {0}" (a list of page pairs).</param>
/// <param name="Footer">"Trang {0}/{1}".</param>
public sealed record ComparisonReportLabels(string Title, string OldFile, string NewFile, string Date, string Summary, string Same, string Added, string Deleted, string Modified, string Pages, string Visual, string Footer);

/// <summary>
/// A comparison written as a PDF (A4, Noto Sans, Vietnamese): the two files, the date, how many changes of each kind,
/// then one line per change — its number, kind (in its colour), pages on both sides and the old and new words — and
/// the pages that look different when compared as pictures.
/// </summary>
public static class ComparisonReport
{
    private const double Margin = 56;
    private const double Width = 595;
    private const double Height = 842;
    private const double TextSize = 10;
    private const int MaxChars = 300;

    public static void Write(Stream output, string oldName, int oldPages, string newName, int newPages, DateTimeOffset date, TextComparison comparison, IReadOnlyList<VisualDifference>? visual, ComparisonReportLabels labels)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(labels);
        FontLibrary.Register();
        var culture = CultureInfo.CurrentCulture;
        var title = new XFont(FontFamilies.NotoSans, 15, XFontStyleEx.Bold);
        var body = new XFont(FontFamilies.NotoSans, TextSize, XFontStyleEx.Regular);
        var bold = new XFont(FontFamilies.NotoSans, TextSize, XFontStyleEx.Bold);
        using var document = new PdfDocument();
        document.Info.Title = labels.Title;

        var lines = new List<(string Text, XFont Font, XBrush Brush, double Indent)>
        {
            (labels.Title, title, XBrushes.Black, 0),
            (string.Format(culture, labels.OldFile, oldName, oldPages), body, XBrushes.Black, 0),
            (string.Format(culture, labels.NewFile, newName, newPages), body, XBrushes.Black, 0),
            (string.Format(culture, labels.Date, date.ToLocalTime().ToString("g", culture)), body, XBrushes.Black, 0),
            (string.Empty, body, XBrushes.Black, 0),
        };
        var changes = comparison.Changes;
        lines.Add((
            changes.Count == 0
                ? labels.Same
                : string.Format(culture, labels.Summary, changes.Count, changes.Count(c => c.Kind == ChangeKind.Added), changes.Count(c => c.Kind == ChangeKind.Deleted), changes.Count(c => c.Kind == ChangeKind.Modified)),
            bold,
            XBrushes.Black,
            0));
        if (visual?.Where(v => !v.IsSame).ToList() is { Count: > 0 } different)
        {
            var pairs = string.Join(", ", different.Select(v => string.Format(culture, labels.Pages, Number(v.Pair.OldPage), Number(v.Pair.NewPage))));
            lines.Add((string.Format(culture, labels.Visual, pairs), body, XBrushes.Black, 0));
        }

        lines.Add((string.Empty, body, XBrushes.Black, 0));
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var (kind, brush) = change.Kind switch
            {
                ChangeKind.Added => (labels.Added, new XSolidBrush(XColor.FromArgb(0, 128, 0))),
                ChangeKind.Deleted => (labels.Deleted, new XSolidBrush(XColor.FromArgb(192, 0, 0))),
                _ => (labels.Modified, new XSolidBrush(XColor.FromArgb(176, 120, 0))),
            };
            var pages = string.Format(culture, labels.Pages, change.OldPage + 1, change.NewPage + 1);
            lines.Add((string.Create(culture, $"{i + 1}. {kind} — {pages}"), bold, brush, 0));
            if (change.OldText.Length > 0)
            {
                lines.Add(("− " + Shorten(change.OldText), body, XBrushes.Black, 14));
            }

            if (change.NewText.Length > 0)
            {
                lines.Add(("+ " + Shorten(change.NewText), body, XBrushes.Black, 14));
            }
        }

        // Lay the lines out, wrapping long ones, on as many pages as needed.
        var pagesWritten = new List<PdfPage>();
        XGraphics? graphics = null;
        var y = Height;
        foreach (var (text, font, brush, indent) in lines)
        {
            var lineHeight = font.Size * 1.45;
            foreach (var part in Wrap(text, font, Width - (2 * Margin) - indent))
            {
                if (graphics is null || y + lineHeight > Height - Margin)
                {
                    graphics?.Dispose();
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(Width);
                    page.Height = XUnit.FromPoint(Height);
                    pagesWritten.Add(page);
                    graphics = XGraphics.FromPdfPage(page);
                    y = Margin;
                }

                graphics.DrawString(part, font, brush, new XPoint(Margin + indent, y + font.Size), XStringFormats.BottomLeft);
                y += lineHeight;
            }
        }

        graphics?.Dispose();
        for (var p = 0; p < pagesWritten.Count; p++)
        {
            using var footer = XGraphics.FromPdfPage(pagesWritten[p], XGraphicsPdfPageOptions.Append);
            footer.DrawString(string.Format(culture, labels.Footer, p + 1, pagesWritten.Count), body, XBrushes.Gray, new XRect(Margin, Height - 40, Width - (2 * Margin), 12), XStringFormats.Center);
        }

        PdfWriting.Save(document, output);
    }

    private static string Number(int? page) => page is { } p ? (p + 1).ToString(CultureInfo.CurrentCulture) : "–";

    private static string Shorten(string text) => text.Length <= MaxChars ? text : text[..MaxChars] + "…";

    private static IEnumerable<string> Wrap(string text, XFont font, double width)
    {
        if (text.Length == 0)
        {
            yield return text;
            yield break;
        }

        var measure = XGraphics.CreateMeasureContext(new XSize(Width, Height), XGraphicsUnit.Point, XPageDirection.Downwards);
        var line = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && measure.MeasureString(candidate, font).Width > width)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }

        yield return line;
    }
}
