using System.Text.Json.Serialization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Annotations;

/// <summary>Text markup of the PDF standard (12.5.6.10).</summary>
public enum MarkupKind
{
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
}

/// <summary>A rectangle or an ellipse drawn in its box (Square and Circle annotations).</summary>
public enum NoteShape
{
    Square,
    Circle,
}

/// <summary>How a line ends (the /LE entry).</summary>
public enum LineEnding
{
    None,
    OpenArrow,
    ClosedArrow,
}

/// <summary>A reviewer's decision on a comment (the "Review" state model of 12.5.6.4).</summary>
public enum ReviewState
{
    None,
    Accepted,
    Rejected,
    Cancelled,
    Completed,
}

/// <summary>
/// One line of marked text as four corners in PDF user space, in the order of /QuadPoints that viewers follow: upper
/// left, upper right, lower left, lower right (as the text reads).
/// </summary>
public readonly record struct TextQuad(PdfPoint UpperLeft, PdfPoint UpperRight, PdfPoint LowerLeft, PdfPoint LowerRight)
{
    /// <summary>The quad of an upright rectangle (a line of horizontal text).</summary>
    public static TextQuad Of(PdfRect rect)
        => new(new PdfPoint(rect.Left, rect.Top), new PdfPoint(rect.Right, rect.Top), new PdfPoint(rect.Left, rect.Bottom), new PdfPoint(rect.Right, rect.Bottom));

    [JsonIgnore]
    public PdfRect Bounds
    {
        get
        {
            PdfPoint[] corners = [UpperLeft, UpperRight, LowerLeft, LowerRight];
            return new PdfRect(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
        }
    }
}

/// <summary>An answer in a comment's conversation.</summary>
public sealed record AnnotationReply(string Id, string Author, DateTimeOffset Modified, string Contents);

/// <summary>A reviewer's state on a comment, as Acrobat records it (a reply carrying a state).</summary>
public sealed record AnnotationReview(string Id, string Author, DateTimeOffset Modified, ReviewState State);

/// <summary>
/// An annotation of the PDF standard as PDF_Pro writes it ("chú thích"): immutable, serializable to JSON (it travels in
/// plan operations and the session manifest), placed in the PDF user space of the page as it is written. Who wrote it
/// and when, its comment text and the conversation under it come with it.
/// </summary>
/// <param name="Id">Unique name of the annotation in its page (/NM).</param>
/// <param name="Color">Its colour (/C).</param>
/// <param name="Opacity">0 (invisible) to 1 (/CA).</param>
/// <param name="Author">Who made it (/T).</param>
/// <param name="Created">When it was made (/CreationDate).</param>
/// <param name="Modified">When it last changed (/M).</param>
/// <param name="Contents">The comment text (/Contents).</param>
[JsonPolymorphic]
[JsonDerivedType(typeof(MarkupAnnotation), "markup")]
[JsonDerivedType(typeof(NoteAnnotation), "note")]
[JsonDerivedType(typeof(FreeTextAnnotation), "freeText")]
[JsonDerivedType(typeof(InkAnnotation), "ink")]
[JsonDerivedType(typeof(ShapeAnnotation), "shape")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(PolyAnnotation), "poly")]
[JsonDerivedType(typeof(StampAnnotation), "stamp")]
public abstract record AnnotationItem(string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents)
{
    /// <summary>Most characters a comment may hold.</summary>
    public const int MaxContentsLength = 20_000;

    /// <summary>The conversation under the comment, oldest first.</summary>
    public IReadOnlyList<AnnotationReply> Replies { get; init; } = [];

    /// <summary>Review states set on the comment, oldest first (the last one counts).</summary>
    public IReadOnlyList<AnnotationReview> Reviews { get; init; } = [];

    /// <summary>The current review state.</summary>
    [JsonIgnore]
    public ReviewState State => Reviews.Count > 0 ? Reviews[^1].State : ReviewState.None;

    /// <summary>Where it is drawn on the page (its /Rect without the room strokes need).</summary>
    [JsonIgnore]
    public abstract PdfRect Bounds { get; }

    /// <summary>The same annotation moved by (<paramref name="dx"/>, <paramref name="dy"/>) points of PDF user space.</summary>
    public abstract AnnotationItem MoveBy(double dx, double dy);

    /// <summary>Problems that make the annotation unusable (none = valid).</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128)
        {
            yield return "The name must be 1 to 128 characters.";
        }

        if (!(Opacity is >= 0 and <= 1))
        {
            yield return "Opacity must be between 0 and 1.";
        }

        if (Contents.Length > MaxContentsLength || Replies.Any(r => r.Contents.Length > MaxContentsLength))
        {
            yield return "A comment can have at most 20000 characters.";
        }

        var bounds = Bounds;
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Top)
            || bounds.Width > OverlayLimits.MaxSide || bounds.Height > OverlayLimits.MaxSide)
        {
            yield return "The annotation must have finite coordinates within 14400 points.";
        }

        foreach (var problem in ValidateCore())
        {
            yield return problem;
        }
    }

    protected abstract IEnumerable<string> ValidateCore();

    protected static PdfPoint Move(PdfPoint point, double dx, double dy) => new(point.X + dx, point.Y + dy);

    protected static PdfRect Move(PdfRect rect, double dx, double dy) => new(rect.Left + dx, rect.Bottom + dy, rect.Right + dx, rect.Top + dy);

    protected static PdfRect BoundsOf(IEnumerable<PdfPoint> points)
    {
        var list = points.ToList();
        return list.Count == 0 ? default : new PdfRect(list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
    }

    protected static IEnumerable<string> CheckWidth(double width)
    {
        if (!(width is > 0 and <= OverlayLimits.MaxStrokeWidth))
        {
            yield return "Line width must be more than 0 and at most 72 points.";
        }
    }
}

/// <summary>Highlight, underline, strike-out or squiggly line over text, one quad per line of it.</summary>
/// <param name="MarkedText">The text it marks (kept for the comments list), or null.</param>
public sealed record MarkupAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    MarkupKind Kind,
    IReadOnlyList<TextQuad> Quads,
    string? MarkedText = null)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => BoundsOf(Quads.SelectMany(q => new[] { q.UpperLeft, q.UpperRight, q.LowerLeft, q.LowerRight }));

    public override AnnotationItem MoveBy(double dx, double dy)
        => this with { Quads = [.. Quads.Select(q => new TextQuad(Move(q.UpperLeft, dx, dy), Move(q.UpperRight, dx, dy), Move(q.LowerLeft, dx, dy), Move(q.LowerRight, dx, dy)))] };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Quads.Count == 0)
        {
            yield return "Marked text needs at least one line.";
        }
    }
}

/// <summary>A sticky note: an icon at <see cref="At"/> (its top-left corner) opening the comment.</summary>
public sealed record NoteAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    PdfPoint At)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    /// <summary>Side of the note's icon in points.</summary>
    public const double IconSize = 20;

    [JsonIgnore]
    public override PdfRect Bounds => new(At.X, At.Y - IconSize, At.X + IconSize, At.Y);

    public override AnnotationItem MoveBy(double dx, double dy) => this with { At = Move(At, dx, dy) };

    protected override IEnumerable<string> ValidateCore() => [];
}

/// <summary>
/// Text shown on the page in a box ("hộp chữ"), optionally with a callout line pointing at something. The text is
/// upright on the page as displayed when it was made: <see cref="Rotation"/> is that page's clockwise rotation.
/// </summary>
/// <param name="Box">The box in PDF user space.</param>
/// <param name="Style">Font (bundled or installed, Vietnamese), size, colour, alignment.</param>
/// <param name="Border">Border colour, or null for none.</param>
/// <param name="Fill">Background colour, or null for none.</param>
/// <param name="Callout">Points of the callout line from the target to the box (first = the arrow's tip), or empty.</param>
/// <param name="Rotation">The page's rotation the text is upright for (0, 90, 180, 270).</param>
public sealed record FreeTextAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    PdfRect Box,
    TextStyle Style,
    RgbColor? Border,
    RgbColor? Fill,
    IReadOnlyList<PdfPoint> Callout,
    int Rotation = 0)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => Callout.Count == 0 ? Box : BoundsOf(Callout.Append(new PdfPoint(Box.Left, Box.Bottom)).Append(new PdfPoint(Box.Right, Box.Top)));

    public override AnnotationItem MoveBy(double dx, double dy)
        => this with { Box = Move(Box, dx, dy), Callout = [.. Callout.Select(p => Move(p, dx, dy))] };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Box.IsEmpty)
        {
            yield return "The text box must have a size.";
        }

        if (!(Style.FontSize is >= OverlayLimits.MinFontSize and <= OverlayLimits.MaxFontSize))
        {
            yield return "Font size must be between 1 and 400 points.";
        }

        if (Rotation is not (0 or 90 or 180 or 270))
        {
            yield return "Rotation must be 0, 90, 180 or 270.";
        }

        if (Callout.Count is 1 or > 3)
        {
            yield return "A callout line has 2 or 3 points.";
        }
    }
}

/// <summary>Free-hand drawing: strokes of points in PDF user space.</summary>
public sealed record InkAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    double Width,
    IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => BoundsOf(Strokes.SelectMany(s => s));

    public override AnnotationItem MoveBy(double dx, double dy)
        => this with { Strokes = [.. Strokes.Select(s => (IReadOnlyList<PdfPoint>)[.. s.Select(p => Move(p, dx, dy))])] };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Strokes.Count == 0 || Strokes.Any(s => s.Count == 0))
        {
            yield return "A drawing needs at least one stroke of points.";
        }

        foreach (var problem in CheckWidth(Width))
        {
            yield return problem;
        }
    }
}

/// <summary>A rectangle (Square) or an ellipse (Circle) in its box.</summary>
public sealed record ShapeAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    NoteShape Shape,
    PdfRect Box,
    double Width,
    RgbColor? Fill)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => Box;

    public override AnnotationItem MoveBy(double dx, double dy) => this with { Box = Move(Box, dx, dy) };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Box.IsEmpty)
        {
            yield return "The shape must have a size.";
        }

        foreach (var problem in CheckWidth(Width))
        {
            yield return problem;
        }
    }
}

/// <summary>A straight line, optionally with arrow heads.</summary>
public sealed record LineAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    PdfPoint Start,
    PdfPoint End,
    double Width,
    LineEnding StartEnding = LineEnding.None,
    LineEnding EndEnding = LineEnding.None)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => BoundsOf([Start, End]);

    public override AnnotationItem MoveBy(double dx, double dy) => this with { Start = Move(Start, dx, dy), End = Move(End, dx, dy) };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Start == End)
        {
            yield return "A line needs two different points.";
        }

        foreach (var problem in CheckWidth(Width))
        {
            yield return problem;
        }
    }
}

/// <summary>A closed polygon or an open polyline through its vertices.</summary>
public sealed record PolyAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    bool Closed,
    IReadOnlyList<PdfPoint> Vertices,
    double Width,
    RgbColor? Fill = null)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => BoundsOf(Vertices);

    public override AnnotationItem MoveBy(double dx, double dy) => this with { Vertices = [.. Vertices.Select(p => Move(p, dx, dy))] };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Vertices.Count < (Closed ? 3 : 2))
        {
            yield return Closed ? "A polygon needs at least 3 points." : "A polyline needs at least 2 points.";
        }

        foreach (var problem in CheckWidth(Width))
        {
            yield return problem;
        }
    }
}

/// <summary>
/// A rubber stamp: words in a frame ("ĐÃ DUYỆT", a date and a name), or a picture. Upright on the page as displayed
/// when it was made (<see cref="Rotation"/>).
/// </summary>
/// <param name="Text">The words (lines separated by "\n"), or null for a picture.</param>
/// <param name="ImagePath">The picture, or null for words.</param>
public sealed record StampAnnotation(
    string Id, RgbColor Color, double Opacity, string Author, DateTimeOffset Created, DateTimeOffset Modified, string Contents,
    PdfRect Box,
    string? Text,
    string? ImagePath = null,
    int Rotation = 0)
    : AnnotationItem(Id, Color, Opacity, Author, Created, Modified, Contents)
{
    [JsonIgnore]
    public override PdfRect Bounds => Box;

    public override AnnotationItem MoveBy(double dx, double dy) => this with { Box = Move(Box, dx, dy) };

    protected override IEnumerable<string> ValidateCore()
    {
        if (Box.IsEmpty)
        {
            yield return "The stamp must have a size.";
        }

        if (string.IsNullOrWhiteSpace(Text) == (ImagePath is null))
        {
            yield return "A stamp has either words or a picture.";
        }

        if (Rotation is not (0 or 90 or 180 or 270))
        {
            yield return "Rotation must be 0, 90, 180 or 270.";
        }
    }
}
