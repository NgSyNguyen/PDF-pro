using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPro.Operations.Ocr;

/// <summary>
/// What an OCR run can be saved as besides the document: plain text (the pages' text, a form feed between pages) and
/// one hOCR document holding every recognised page. The boxes of hOCR are those of the picture OCR read (the page
/// rendered at the run's resolution, turned upright and straightened as the report says).
/// </summary>
public static partial class OcrExports
{
    /// <summary>The text of the recognised pages, UTF-8 without BOM, Windows line ends, a form feed between pages.</summary>
    public static byte[] Text(OcrReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = report.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }

    /// <summary>
    /// One hOCR document with every recognised page that has hOCR, in order: the head of the first, then each page's
    /// body with its ids and page number made unique; the path of the temporary picture Tesseract read is left out.
    /// Null when no page has hOCR.
    /// </summary>
    public static string? Hocr(OcrReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var pages = report.Pages.Where(p => p.Outcome == OcrPageOutcome.Recognized && !string.IsNullOrEmpty(p.Hocr)).ToList();
        if (pages.Count == 0)
        {
            return null;
        }

        var first = pages[0].Hocr!;
        var bodyAt = first.IndexOf("<body>", StringComparison.Ordinal);
        var head = bodyAt >= 0 ? first[..(bodyAt + "<body>".Length)] : "<html><body>";
        var output = new StringBuilder(head).Append('\n');
        foreach (var page in pages)
        {
            var number = page.PageIndex + 1;
            var body = Body(page.Hocr!);
            body = Ids().Replace(body, m => $"id='{m.Groups[1].Value}_{number.ToString(CultureInfo.InvariantCulture)}{m.Groups[2].Value}'");
            body = PageNumber().Replace(body, "ppageno " + page.PageIndex.ToString(CultureInfo.InvariantCulture));
            body = ImagePath().Replace(body, string.Empty);
            output.Append(body.Trim('\n', '\r', ' ')).Append('\n');
        }

        return output.Append("</body>\n</html>\n").ToString();
    }

    private static string Body(string hocr)
    {
        var start = hocr.IndexOf("<body>", StringComparison.Ordinal);
        var end = hocr.LastIndexOf("</body>", StringComparison.Ordinal);
        return start >= 0 && end > start ? hocr[(start + "<body>".Length)..end] : hocr;
    }

    /// <summary>Tesseract's ids: page_1, block_1_2, par_1_3, line_1_4, word_1_5 (the 1 is the page of its run).</summary>
    [GeneratedRegex(@"id='([a-z]+(?:_[a-z]+)*)_1((?:_\d+)?)'")]
    private static partial Regex Ids();

    [GeneratedRegex(@"ppageno \d+")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"image ""[^""]*""; ?")]
    private static partial Regex ImagePath();
}
