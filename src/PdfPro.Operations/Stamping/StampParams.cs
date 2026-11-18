using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Core.Text;

namespace PdfPro.Operations.Stamping;

/// <summary>The kinds of removable stamps PDF_Pro adds, as marked in the file.</summary>
public enum StampKind
{
    Watermark,
    Background,
    HeaderFooter,
    PageNumbers,
    Bates,
}

public enum BackgroundFit
{
    /// <summary>The picture covers the whole page, stretched if its proportions differ.</summary>
    Stretch,

    /// <summary>The picture keeps its proportions and fits inside the page, centred.</summary>
    Fit,
}

/// <param name="Text">The watermark text (one or more lines); either this or <paramref name="ImagePath"/>.</param>
/// <param name="ImagePath">A picture to use instead of text.</param>
/// <param name="Style">Font, size, colour of the text (default: Noto Sans Bold 60 pt, grey).</param>
/// <param name="ImageScale">Width of the picture as a fraction of the page width.</param>
/// <param name="Opacity">0 (invisible) to 1 (opaque).</param>
/// <param name="Rotation">Degrees counter-clockwise as seen on the page (45 rises from bottom left to top right).</param>
/// <param name="Anchor">One of nine places on the page (ignored when tiled).</param>
/// <param name="MarginX">Distance from the left or right edge for side anchors, in points.</param>
/// <param name="MarginY">Distance from the top or bottom edge for top and bottom anchors, in points.</param>
/// <param name="Tile">Repeat over the whole page.</param>
/// <param name="TileGap">Space between repeated copies, in points.</param>
/// <param name="BehindContent">Under the page content instead of over it (hidden by opaque scans).</param>
/// <param name="Pages">Page range ("1-3, 8", "lẻ", "chẵn"…); null or empty means all pages.</param>
public sealed record WatermarkParams(
    string? Text = null,
    string? ImagePath = null,
    TextStyle? Style = null,
    [property: Range(0.01, 4)] double ImageScale = 0.5,
    [property: Range(0.0, 1.0)] double Opacity = 0.3,
    [property: Range(-360.0, 360.0)] double Rotation = 45,
    StampAnchor Anchor = StampAnchor.Center,
    [property: Range(0.0, 2000.0)] double MarginX = 36,
    [property: Range(0.0, 2000.0)] double MarginY = 36,
    bool Tile = false,
    [property: Range(0.0, 2000.0)] double TileGap = 72,
    bool BehindContent = false,
    string? Pages = null)
{
    public static TextStyle DefaultStyle { get; } = new(FontFamilies.NotoSans, 60, new RgbColor(160, 160, 160), Bold: true, Alignment: TextAlignment.Center);
}

/// <param name="Color">A plain colour; either this or <paramref name="ImagePath"/>.</param>
/// <param name="ImagePath">A picture (letterhead, paper texture).</param>
/// <param name="Fit">How the picture covers the page.</param>
/// <param name="Opacity">0 to 1.</param>
/// <param name="Pages">Page range; null or empty means all pages.</param>
public sealed record BackgroundParams(
    RgbColor? Color = null,
    string? ImagePath = null,
    BackgroundFit Fit = BackgroundFit.Stretch,
    [property: Range(0.0, 1.0)] double Opacity = 1,
    string? Pages = null);

/// <summary>
/// Text in six places (left, centre, right of the top and of the bottom), with the fields of
/// <see cref="StampTemplate"/>: {page}, {pages}, {date:…}, {filename}, {title}.
/// </summary>
/// <param name="Style">Font, size and colour (default Noto Sans 9 pt black).</param>
/// <param name="MarginX">Distance from the left and right edges, in points.</param>
/// <param name="MarginY">Distance from the top and bottom edges, in points.</param>
/// <param name="SkipFirstPage">No header or footer on the first chosen page (a cover); numbering starts after it.</param>
/// <param name="StartNumber">{page} of the first numbered page.</param>
/// <param name="NumberStyle">How {page} and {pages} are written.</param>
/// <param name="Date">The date of {date}; null means when the operation runs.</param>
/// <param name="FileName">The name for {filename}; null means the input file's name.</param>
/// <param name="Pages">Page range; null or empty means all pages.</param>
public sealed record HeaderFooterParams(
    string? TopLeft = null,
    string? TopCenter = null,
    string? TopRight = null,
    string? BottomLeft = null,
    string? BottomCenter = null,
    string? BottomRight = null,
    TextStyle? Style = null,
    [property: Range(0.0, 2000.0)] double MarginX = 36,
    [property: Range(0.0, 2000.0)] double MarginY = 24,
    bool SkipFirstPage = false,
    [property: Range(-1_000_000, 1_000_000)] int StartNumber = 1,
    PageNumberStyle NumberStyle = PageNumberStyle.Arabic,
    DateTime? Date = null,
    string? FileName = null,
    string? Pages = null)
{
    public static TextStyle DefaultStyle { get; } = new(FontFamilies.NotoSans, 9, RgbColor.Black);
}

/// <param name="Pattern">The text, with {page} and {pages} ("Trang {page}/{pages}").</param>
/// <param name="Style">1, 2, 3 or i, ii, iii or I, II, III.</param>
/// <param name="Anchor">Where on the page.</param>
/// <param name="TextStyle">Font, size and colour (default Noto Sans 10 pt black).</param>
/// <param name="FirstPage">The 1-based page that gets the first number; earlier pages get none.</param>
/// <param name="StartNumber">The number written on <paramref name="FirstPage"/>.</param>
/// <param name="Pages">Page range; null or empty means all pages.</param>
/// <param name="FileName">The name for {filename}; null means the input file's name.</param>
public sealed record PageNumbersParams(
    [property: Required] string Pattern = "{page}",
    PageNumberStyle Style = PageNumberStyle.Arabic,
    StampAnchor Anchor = StampAnchor.BottomCenter,
    TextStyle? TextStyle = null,
    [property: Range(0.0, 2000.0)] double MarginX = 36,
    [property: Range(0.0, 2000.0)] double MarginY = 24,
    [property: Range(1, int.MaxValue)] int FirstPage = 1,
    [property: Range(-1_000_000, 1_000_000)] int StartNumber = 1,
    string? Pages = null,
    string? FileName = null)
{
    public static TextStyle DefaultStyle { get; } = new(FontFamilies.NotoSans, 10, RgbColor.Black);
}

/// <param name="Prefix">Text before the number ("HS-").</param>
/// <param name="Suffix">Text after the number.</param>
/// <param name="Digits">Minimum number of digits (zero-padded).</param>
/// <param name="StartNumber">Number of the first page of the first file.</param>
/// <param name="Anchor">Where on the page.</param>
/// <param name="Style">Font, size and colour (default Noto Sans Bold 10 pt black).</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (otherwise " (2)" is added).</param>
public sealed record BatesParams(
    [property: StringLength(BatesOperation.MaxAffixLength)] string Prefix = "",
    [property: StringLength(BatesOperation.MaxAffixLength)] string Suffix = "",
    [property: Range(1, BatesFormat.MaxDigits)] int Digits = 6,
    [property: Range(0L, 999_999_999_999L)] long StartNumber = 1,
    StampAnchor Anchor = StampAnchor.BottomRight,
    TextStyle? Style = null,
    [property: Range(0.0, 2000.0)] double MarginX = 36,
    [property: Range(0.0, 2000.0)] double MarginY = 24,
    bool Overwrite = false)
{
    public static TextStyle DefaultStyle { get; } = new(FontFamilies.NotoSans, 10, RgbColor.Black, Bold: true);
}

/// <param name="Kinds">Which stamps to take off.</param>
/// <param name="Pages">Page range; null or empty means all pages.</param>
public sealed record RemoveStampsParams([property: Required, MinLength(1)] IReadOnlyList<StampKind> Kinds, string? Pages = null);
