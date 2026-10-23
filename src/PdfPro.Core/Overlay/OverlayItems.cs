using System.Text.Json.Serialization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Core.Overlay;

public enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify,
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,

    /// <summary>A plain white (or chosen colour) rectangle without border that hides what is under it — visually only.</summary>
    Whiteout,
}

/// <summary>How text is drawn: family (bundled or installed), size in points, colour, weight, slant, alignment, line spacing.</summary>
/// <param name="LineSpacing">Multiplier of the font's natural line height (1 = single).</param>
public sealed record TextStyle(
    string FontFamily,
    double FontSize,
    RgbColor Color,
    bool Bold = false,
    bool Italic = false,
    TextAlignment Alignment = TextAlignment.Left,
    double LineSpacing = 1)
{
    public static TextStyle Default { get; } = new(FontFamilies.Default, 12, RgbColor.Black);
}

/// <summary>An outline: colour and width in points.</summary>
public sealed record StrokeStyle(RgbColor Color, double Width);

/// <summary>
/// Where a boxed object sits on its page: the centre in the page's PDF user space, its own width and height in points,
/// and its rotation in degrees clockwise relative to the page as displayed. Keeping the centre in PDF space (and
/// converting only through <see cref="PageCoordinateMapper"/>) is what keeps objects in place at every zoom and on
/// pages with /Rotate.
/// </summary>
public readonly record struct OverlayBox(PdfPoint Center, PdfSize Size, double Rotation = 0)
{
    /// <summary>The box on screen for a page drawn by <paramref name="mapper"/>.</summary>
    public RotatedBox ToView(PageCoordinateMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return new RotatedBox(mapper.PdfToScreen(Center), Size.Width * mapper.Scale, Size.Height * mapper.Scale, Rotation);
    }

    /// <summary>The box for an on-screen box of a page drawn by <paramref name="mapper"/>.</summary>
    public static OverlayBox FromView(RotatedBox box, PageCoordinateMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return new OverlayBox(mapper.ScreenToPdf(box.Center), new PdfSize(box.Width / mapper.Scale, box.Height / mapper.Scale), RotatedBox.NormalizeAngle(box.Angle));
    }

    /// <summary>
    /// The transform from the box's own space — origin at its top-left, x to the right, y down, in points — to the
    /// page's PDF user space: the "cm" under which the object's content is drawn.
    /// </summary>
    public PdfMatrix LocalToPdf(PageGeometry page)
    {
        var mapper = new PageCoordinateMapper(page, 1);
        var center = mapper.PdfToScreen(Center);
        return PdfMatrix.Translation(-Size.Width / 2, -Size.Height / 2)
            .Then(PdfMatrix.Rotation(Rotation))
            .Then(PdfMatrix.Translation(center.X, center.Y))
            .Then(mapper.ScreenToPdfMatrix);
    }
}

/// <summary>
/// An object placed on a page but not yet written into the PDF ("đối tượng tạm"). Records are immutable and serialize to
/// JSON (the parameters of edit.overlay). <see cref="PageIndex"/> is 0-based in the document as shown.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(TextOverlay), "text")]
[JsonDerivedType(typeof(ImageOverlay), "image")]
[JsonDerivedType(typeof(ShapeOverlay), "shape")]
[JsonDerivedType(typeof(LineOverlay), "line")]
public abstract record OverlayItem(int PageIndex, double Opacity)
{
    /// <summary>Problems that make the object unusable on a document of <paramref name="pageCount"/> pages (none = valid).</summary>
    public IEnumerable<string> Validate(int pageCount)
    {
        if (PageIndex < 0 || PageIndex >= pageCount)
        {
            yield return $"Page {PageIndex + 1} does not exist.";
        }

        if (!(Opacity is >= 0 and <= 1))
        {
            yield return "Opacity must be between 0 and 1.";
        }

        foreach (var problem in ValidateCore())
        {
            yield return problem;
        }
    }

    /// <summary>The object moved to another page (or the same one), keeping its place as displayed, shifted by <paramref name="offset"/> points.</summary>
    public abstract OverlayItem MoveToPage(PageGeometry from, PageGeometry to, int pageIndex, ViewPoint offset = default);

    protected abstract IEnumerable<string> ValidateCore();

    protected static IEnumerable<string> ValidateBox(OverlayBox box)
    {
        if (!double.IsFinite(box.Center.X) || !double.IsFinite(box.Center.Y) || !double.IsFinite(box.Rotation))
        {
            yield return "The position must be a finite number.";
        }

        if (!(box.Size.Width is > 0 and <= OverlayLimits.MaxSide) || !(box.Size.Height is > 0 and <= OverlayLimits.MaxSide))
        {
            yield return "Width and height must be positive and at most 14400 points.";
        }
    }

    protected static IEnumerable<string> ValidateStroke(StrokeStyle? stroke)
    {
        if (stroke is not null && !(stroke.Width is >= 0 and <= OverlayLimits.MaxStrokeWidth))
        {
            yield return "Border width must be between 0 and 72 points.";
        }
    }

    protected static OverlayBox MoveBox(OverlayBox box, PageGeometry from, PageGeometry to, ViewPoint offset)
        => box with { Center = MovePoint(box.Center, from, to, offset) };

    protected static PdfPoint MovePoint(PdfPoint point, PageGeometry from, PageGeometry to, ViewPoint offset)
    {
        var shown = new PageCoordinateMapper(from, 1).PdfToScreen(point);
        return new PageCoordinateMapper(to, 1).ScreenToPdf(shown + offset);
    }
}

/// <summary>A text box: the text is laid out inside <see cref="Box"/> (less <see cref="Padding"/>) by <see cref="TextLayout"/>.</summary>
public sealed record TextOverlay(
    int PageIndex,
    OverlayBox Box,
    string Text,
    TextStyle Style,
    RgbColor? Fill = null,
    StrokeStyle? Border = null,
    double Padding = OverlayLimits.DefaultPadding,
    double Opacity = 1) : OverlayItem(PageIndex, Opacity)
{
    public override OverlayItem MoveToPage(PageGeometry from, PageGeometry to, int pageIndex, ViewPoint offset = default)
        => this with { PageIndex = pageIndex, Box = MoveBox(Box, from, to, offset) };

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var problem in ValidateBox(Box).Concat(ValidateStroke(Border)))
        {
            yield return problem;
        }

        if (Text is null || Text.Length > OverlayLimits.MaxTextLength)
        {
            yield return "The text is missing or longer than 20000 characters.";
        }

        if (Style is null || string.IsNullOrWhiteSpace(Style.FontFamily))
        {
            yield return "A font family is required.";
        }
        else
        {
            if (!(Style.FontSize is >= OverlayLimits.MinFontSize and <= OverlayLimits.MaxFontSize))
            {
                yield return "Font size must be between 1 and 400 points.";
            }

            if (!(Style.LineSpacing is >= OverlayLimits.MinLineSpacing and <= OverlayLimits.MaxLineSpacing))
            {
                yield return "Line spacing must be between 0.5 and 5.";
            }
        }

        if (!(Padding is >= 0 and <= OverlayLimits.MaxPadding))
        {
            yield return "Padding must be between 0 and 72 points.";
        }
    }
}

/// <summary>A picture from a file (PNG keeps transparency, JPEG is embedded as it is); <see cref="IsSignature"/> marks a hand signature image.</summary>
public sealed record ImageOverlay(int PageIndex, OverlayBox Box, string ImagePath, bool IsSignature = false, double Opacity = 1) : OverlayItem(PageIndex, Opacity)
{
    public override OverlayItem MoveToPage(PageGeometry from, PageGeometry to, int pageIndex, ViewPoint offset = default)
        => this with { PageIndex = pageIndex, Box = MoveBox(Box, from, to, offset) };

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var problem in ValidateBox(Box))
        {
            yield return problem;
        }

        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            yield return "An image file is required.";
        }
    }
}

/// <summary>A rectangle, ellipse or whiteout; a whiteout is filled (white unless <see cref="Fill"/> says otherwise) and has no border.</summary>
public sealed record ShapeOverlay(int PageIndex, OverlayBox Box, ShapeKind Kind, StrokeStyle? Stroke = null, RgbColor? Fill = null, double Opacity = 1) : OverlayItem(PageIndex, Opacity)
{
    /// <summary>The border actually drawn (a whiteout has none).</summary>
    public StrokeStyle? EffectiveStroke => Kind == ShapeKind.Whiteout ? null : Stroke;

    /// <summary>The fill actually drawn (a whiteout is always filled).</summary>
    public RgbColor? EffectiveFill => Kind == ShapeKind.Whiteout ? Fill ?? RgbColor.White : Fill;

    public override OverlayItem MoveToPage(PageGeometry from, PageGeometry to, int pageIndex, ViewPoint offset = default)
        => this with { PageIndex = pageIndex, Box = MoveBox(Box, from, to, offset) };

    protected override IEnumerable<string> ValidateCore()
    {
        foreach (var problem in ValidateBox(Box).Concat(ValidateStroke(Stroke)))
        {
            yield return problem;
        }

        if (EffectiveStroke is not { Width: > 0 } && EffectiveFill is null)
        {
            yield return "A shape needs a border or a fill.";
        }
    }
}

/// <summary>A straight line between two points of the page's PDF user space, optionally with arrow heads.</summary>
public sealed record LineOverlay(int PageIndex, PdfPoint Start, PdfPoint End, StrokeStyle Stroke, bool ArrowAtStart = false, bool ArrowAtEnd = false, double Opacity = 1) : OverlayItem(PageIndex, Opacity)
{
    /// <summary>Length of an arrow head for a line width (points).</summary>
    public static double ArrowLength(double strokeWidth) => Math.Max(6, strokeWidth * 4);

    public override OverlayItem MoveToPage(PageGeometry from, PageGeometry to, int pageIndex, ViewPoint offset = default)
        => this with { PageIndex = pageIndex, Start = MovePoint(Start, from, to, offset), End = MovePoint(End, from, to, offset) };

    protected override IEnumerable<string> ValidateCore()
    {
        if (!double.IsFinite(Start.X) || !double.IsFinite(Start.Y) || !double.IsFinite(End.X) || !double.IsFinite(End.Y))
        {
            yield return "The position must be a finite number.";
        }

        if (Stroke is null || !(Stroke.Width is > 0 and <= OverlayLimits.MaxStrokeWidth))
        {
            yield return "Line width must be more than 0 and at most 72 points.";
        }

        if (Start == End)
        {
            yield return "A line needs two different points.";
        }
    }
}

/// <summary>Bounds of overlay values, shared by validation and the editing UI.</summary>
public static class OverlayLimits
{
    public const double MaxSide = 14400;

    public const double MaxStrokeWidth = 72;

    public const double MinFontSize = 1;

    public const double MaxFontSize = 400;

    public const double MinLineSpacing = 0.5;

    public const double MaxLineSpacing = 5;

    public const double MaxPadding = 72;

    public const double DefaultPadding = 2;

    public const int MaxTextLength = 20000;
}
