namespace PdfPro.Core.Web;

/// <summary>The paper a web page is printed on.</summary>
public enum WebPaper
{
    A4,
    A3,
    A5,
    Letter,
    Legal,
}

/// <summary>The space around a printed web page.</summary>
public enum WebMargins
{
    /// <summary>None: the page's own layout reaches the paper's edges.</summary>
    None,

    /// <summary>6 mm.</summary>
    Narrow,

    /// <summary>12 mm (the default).</summary>
    Normal,

    /// <summary>25 mm.</summary>
    Wide,
}

/// <summary>How a web page becomes a PDF.</summary>
/// <param name="Paper">The paper size.</param>
/// <param name="Landscape">Turned sideways.</param>
/// <param name="Margins">The space around the page.</param>
/// <param name="Backgrounds">Background colours and pictures are printed (as on screen), not left white.</param>
/// <param name="HeaderAndFooter">The browser's line of title and date above each page, and address and page number below.</param>
public sealed record WebPrintSettings(
    WebPaper Paper = WebPaper.A4,
    bool Landscape = false,
    WebMargins Margins = WebMargins.Normal,
    bool Backgrounds = true,
    bool HeaderAndFooter = false)
{
    private const double MillimetresPerInch = 25.4;

    /// <summary>The paper's width and height in inches, upright.</summary>
    public static (double Width, double Height) PaperInches(WebPaper paper) => paper switch
    {
        WebPaper.A4 => (210 / MillimetresPerInch, 297 / MillimetresPerInch),
        WebPaper.A3 => (297 / MillimetresPerInch, 420 / MillimetresPerInch),
        WebPaper.A5 => (148 / MillimetresPerInch, 210 / MillimetresPerInch),
        WebPaper.Letter => (8.5, 11),
        WebPaper.Legal => (8.5, 14),
        _ => throw new ArgumentOutOfRangeException(nameof(paper), paper, null),
    };

    /// <summary>The space around the page, in inches.</summary>
    public static double MarginInches(WebMargins margins) => margins switch
    {
        WebMargins.None => 0,
        WebMargins.Narrow => 6 / MillimetresPerInch,
        WebMargins.Normal => 12 / MillimetresPerInch,
        WebMargins.Wide => 25 / MillimetresPerInch,
        _ => throw new ArgumentOutOfRangeException(nameof(margins), margins, null),
    };

    /// <summary>The page's width and height in PDF points, as printed (turned when <see cref="Landscape"/>).</summary>
    public (double Width, double Height) PagePoints()
    {
        var (width, height) = PaperInches(Paper);
        return Landscape ? (height * 72, width * 72) : (width * 72, height * 72);
    }
}
