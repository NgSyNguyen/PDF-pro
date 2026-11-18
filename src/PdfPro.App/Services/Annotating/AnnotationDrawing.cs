using System.Windows;
using System.Windows.Media;
using PdfPro.App.Services.Editing;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.Services.Annotating;

/// <summary>
/// Draws annotations of the session on screen the way <c>AnnotationWriter</c> writes their appearance: same places,
/// same strokes, text laid out the same way with the same faces. Highlights are drawn translucent (the PDF multiplies
/// their colour, which WPF cannot). Every place goes through <see cref="PageCoordinateMapper"/>.
/// </summary>
public static class AnnotationDrawing
{
    /// <summary>How strongly a highlight covers on screen (the file multiplies instead).</summary>
    public const double HighlightAlpha = 0.4;

    /// <summary>Room between a text box's edge and its text, in points (as written).</summary>
    public const double TextPadding = 4;

    private static readonly Brush NoteLines = Freeze(new SolidColorBrush(Color.FromRgb(64, 64, 64)));

    /// <summary>Text boxes have no pictures.</summary>
    private static readonly ScreenImages NoImages = new();

    /// <param name="context">Where to draw (device-independent pixels of the page as shown).</param>
    /// <param name="item">The annotation.</param>
    /// <param name="mapper">The page as shown: its rotation and scale.</param>
    /// <param name="fonts">Faces for text (null: text is left out, as in tests).</param>
    /// <param name="images">Pictures of picture stamps.</param>
    /// <param name="pixelsPerDip">The screen's pixels per device-independent pixel.</param>
    public static void Draw(DrawingContext context, AnnotationItem item, PageCoordinateMapper mapper, ScreenFonts? fonts, ScreenImages images, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(images);
        var opacity = item is MarkupAnnotation { Kind: MarkupKind.Highlight } ? item.Opacity * HighlightAlpha : item.Opacity;
        context.PushOpacity(opacity);
        switch (item)
        {
            case MarkupAnnotation markup:
                DrawMarkup(context, markup, mapper);
                break;
            case NoteAnnotation note:
                DrawNote(context, note, mapper);
                break;
            case FreeTextAnnotation freeText:
                DrawFreeText(context, freeText, mapper, fonts, pixelsPerDip);
                break;
            case InkAnnotation ink:
                DrawInk(context, ink, mapper);
                break;
            case ShapeAnnotation shape:
                DrawShape(context, shape, mapper);
                break;
            case LineAnnotation line:
                DrawLine(context, mapper, line.Color, line.Start, line.End, line.Width, line.StartEnding, line.EndEnding);
                break;
            case PolyAnnotation poly:
                DrawPoly(context, poly, mapper);
                break;
            case StampAnnotation stamp:
                DrawStamp(context, stamp, mapper, fonts, images, pixelsPerDip);
                break;
        }

        context.Pop();
    }

    /// <summary>Where the annotation is on screen (for the selection frame).</summary>
    public static Rect ScreenBounds(AnnotationItem item, PageCoordinateMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(mapper);
        var bounds = mapper.PdfRectToScreen(item.Bounds);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static Brush Brush(RgbColor color) => Freeze(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)));

    private static void DrawMarkup(DrawingContext context, MarkupAnnotation markup, PageCoordinateMapper mapper)
    {
        var brush = Brush(markup.Color);
        foreach (var quad in markup.Quads)
        {
            var (ul, ur, ll, lr) = (Screen(mapper, quad.UpperLeft), Screen(mapper, quad.UpperRight), Screen(mapper, quad.LowerLeft), Screen(mapper, quad.LowerRight));
            var height = (ul - ll).Length;
            var up = height > 0 ? (ul - ll) / height : new Vector(0, -1);
            switch (markup.Kind)
            {
                case MarkupKind.Highlight:
                    context.DrawGeometry(brush, null, Polygon([ul, ur, lr, ll], closed: true));
                    break;
                case MarkupKind.Underline:
                case MarkupKind.StrikeOut:
                    var width = Math.Max(0.5 * mapper.Scale, height / 14);
                    var offset = markup.Kind == MarkupKind.Underline ? width : height * 0.5;
                    context.DrawLine(new Pen(brush, width), ll + (up * offset), lr + (up * offset));
                    break;
                case MarkupKind.Squiggly:
                    var along = lr - ll;
                    var length = along.Length;
                    if (length <= 0)
                    {
                        break;
                    }

                    along /= length;
                    var step = Math.Max(mapper.Scale, height / 6);
                    var amplitude = Math.Max(0.75 * mapper.Scale, height / 12);
                    var points = new List<Point> { ll };
                    var high = true;
                    for (var x = step; x <= length + 0.01; x += step, high = !high)
                    {
                        points.Add(ll + (along * Math.Min(x, length)) + (up * (high ? amplitude : 0)));
                    }

                    context.DrawGeometry(null, new Pen(brush, Math.Max(0.5 * mapper.Scale, height / 18)), Polygon(points, closed: false));
                    break;
            }
        }
    }

    /// <summary>A speech bubble with three lines, upright on screen, filling the note's box as shown.</summary>
    private static void DrawNote(DrawingContext context, NoteAnnotation note, PageCoordinateMapper mapper)
    {
        var box = ScreenBounds(note, mapper);
        var unit = box.Width / NoteAnnotation.IconSize;
        Point P(double x, double y) => new(box.X + (x * unit), box.Y + (y * unit));
        var outline = new Pen(NoteLines, unit);
        context.DrawGeometry(Brush(note.Color), outline, Polygon([P(1.5, 1.5), P(18.5, 1.5), P(18.5, 14), P(9, 14), P(4, 18.5), P(5, 14), P(1.5, 14)], closed: true));
        foreach (var y in new[] { 6.0, 9.5, 13.0 })
        {
            context.DrawLine(outline, P(4.5, y), P(15.5, y));
        }
    }

    private static void DrawFreeText(DrawingContext context, FreeTextAnnotation freeText, PageCoordinateMapper mapper, ScreenFonts? fonts, double pixelsPerDip)
    {
        if (freeText.Callout.Count > 1)
        {
            var points = freeText.Callout.Select(p => Screen(mapper, p)).ToList();
            var brush = Brush(freeText.Color);
            var pen = new Pen(brush, Math.Max(1, mapper.Scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.DrawGeometry(null, pen, Polygon(points, closed: false));
            OpenArrow(context, pen, points[0], points[1], LineOverlay.ArrowLength(1) * mapper.Scale);
        }

        // The text is upright for the page's rotation when it was made; the page may have been turned since.
        var own = freeText.Rotation is 90 or 270 ? new PdfSize(freeText.Box.Height, freeText.Box.Width) : freeText.Box.Size;
        var center = new PdfPoint((freeText.Box.Left + freeText.Box.Right) / 2, (freeText.Box.Bottom + freeText.Box.Top) / 2);
        if (fonts is null)
        {
            return;
        }

        var angle = (((mapper.RotationDegrees - freeText.Rotation) % 360) + 360) % 360;
        var text = new TextOverlay(0, new OverlayBox(center, own, angle), freeText.Contents, freeText.Style, freeText.Fill, freeText.Border is { } border ? new StrokeStyle(border, 1) : null, TextPadding);
        OverlayDrawing.Draw(context, text, mapper, fonts, NoImages, pixelsPerDip);
    }

    private static void DrawInk(DrawingContext context, InkAnnotation ink, PageCoordinateMapper mapper)
    {
        var brush = Brush(ink.Color);
        var width = ink.Width * mapper.Scale;
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var stroke in ink.Strokes)
        {
            if (stroke.Count == 1)
            {
                context.DrawEllipse(brush, null, Screen(mapper, stroke[0]), width / 2, width / 2);
                continue;
            }

            context.DrawGeometry(null, pen, Polygon([.. stroke.Select(p => Screen(mapper, p))], closed: false));
        }
    }

    private static void DrawShape(DrawingContext context, ShapeAnnotation shape, PageCoordinateMapper mapper)
    {
        var box = ScreenBounds(shape, mapper);
        var width = shape.Width * mapper.Scale;
        if (box.Width > width && box.Height > width)
        {
            box.Inflate(-width / 2, -width / 2);
        }

        var pen = new Pen(Brush(shape.Color), width);
        var fill = shape.Fill is { } color ? Brush(color) : null;
        if (shape.Shape == NoteShape.Circle)
        {
            context.DrawEllipse(fill, pen, new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2)), box.Width / 2, box.Height / 2);
        }
        else
        {
            context.DrawRectangle(fill, pen, box);
        }
    }

    private static void DrawPoly(DrawingContext context, PolyAnnotation poly, PageCoordinateMapper mapper)
    {
        var pen = new Pen(Brush(poly.Color), poly.Width * mapper.Scale) { LineJoin = PenLineJoin.Round };
        var fill = poly.Closed && poly.Fill is { } color ? Brush(color) : null;
        context.DrawGeometry(fill, pen, Polygon([.. poly.Vertices.Select(p => Screen(mapper, p))], poly.Closed));
    }

    private static void DrawLine(DrawingContext context, PageCoordinateMapper mapper, RgbColor color, PdfPoint startPoint, PdfPoint endPoint, double lineWidth, LineEnding startEnding, LineEnding endEnding)
    {
        var (start, end) = (Screen(mapper, startPoint), Screen(mapper, endPoint));
        var length = (end - start).Length;
        if (length < 1e-6)
        {
            return;
        }

        var brush = Brush(color);
        var pen = new Pen(brush, lineWidth * mapper.Scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var head = Math.Min(LineOverlay.ArrowLength(lineWidth) * mapper.Scale, length / 2);
        var direction = (end - start) / length;
        var from = startEnding == LineEnding.ClosedArrow ? start + (direction * head) : start;
        var to = endEnding == LineEnding.ClosedArrow ? end - (direction * head) : end;
        context.DrawLine(pen, from, to);
        Ending(context, pen, brush, start, end, head, startEnding);
        Ending(context, pen, brush, end, start, head, endEnding);
    }

    private static void Ending(DrawingContext context, Pen pen, Brush brush, Point tip, Point from, double head, LineEnding ending)
    {
        if (ending == LineEnding.OpenArrow)
        {
            OpenArrow(context, pen, tip, from, head);
        }
        else if (ending == LineEnding.ClosedArrow)
        {
            var (left, right) = Wings(tip, from, head);
            context.DrawGeometry(brush, null, Polygon([tip, left, right], closed: true));
        }
    }

    private static void OpenArrow(DrawingContext context, Pen pen, Point tip, Point from, double head)
    {
        var (left, right) = Wings(tip, from, Math.Min(head, (from - tip).Length / 2));
        context.DrawGeometry(null, pen, Polygon([left, tip, right], closed: false));
    }

    /// <summary>The two back corners of an arrow head as wide as half its length (as written).</summary>
    private static (Point Left, Point Right) Wings(Point tip, Point from, double length)
    {
        var back = from - tip;
        if (back.Length > 0)
        {
            back.Normalize();
        }

        var basePoint = tip + (back * length);
        var side = new Vector(-back.Y, back.X);
        return (basePoint + (side * length / 2), basePoint - (side * length / 2));
    }

    private static void DrawStamp(DrawingContext context, StampAnnotation stamp, PageCoordinateMapper mapper, ScreenFonts? fonts, ScreenImages images, double pixelsPerDip)
    {
        var own = stamp.Rotation is 90 or 270 ? new PdfSize(stamp.Box.Height, stamp.Box.Width) : stamp.Box.Size;
        var center = new PdfPoint((stamp.Box.Left + stamp.Box.Right) / 2, (stamp.Box.Bottom + stamp.Box.Top) / 2);
        var angle = (((mapper.RotationDegrees - stamp.Rotation) % 360) + 360) % 360;
        if (stamp.ImagePath is { } path)
        {
            // Pictures do not use the fonts.
            OverlayDrawing.Draw(context, new ImageOverlay(0, new OverlayBox(center, own, angle), path), mapper, fonts!, images, pixelsPerDip);
            return;
        }

        // Local space: the stamp's top-left at the origin, in points, then scaled and turned like the page.
        var shown = new OverlayBox(center, own, angle).ToView(mapper);
        var local = PdfMatrix.Translation(-own.Width / 2, -own.Height / 2)
            .Then(PdfMatrix.Scaling(mapper.Scale, mapper.Scale))
            .Then(PdfMatrix.Rotation(shown.Angle))
            .Then(PdfMatrix.Translation(shown.Center.X, shown.Center.Y));
        context.PushTransform(new MatrixTransform(OverlayDrawing.ToMatrix(local)));
        var brush = Brush(stamp.Color);
        context.DrawRoundedRectangle(null, new Pen(brush, 2), new Rect(1, 1, Math.Max(0, own.Width - 2), Math.Max(0, own.Height - 2)), 8, 8);
        if (fonts is not null)
        {
            DrawStampText(context, stamp, own, fonts, brush, pixelsPerDip * mapper.Scale);
        }

        context.Pop();
    }

    /// <summary>The words centred in the frame, bold, shrunk to fit as the writer does.</summary>
    private static void DrawStampText(DrawingContext context, StampAnnotation stamp, PdfSize own, ScreenFonts fonts, Brush brush, double pixelsPerDip)
    {
        var lines = TextNormalizer.ToNfc(stamp.Text ?? string.Empty).Split('\n');
        var size = Math.Min(own.Height * 0.8 / lines.Length, 40);
        var metrics = FontLibrary.Instance.Metrics(FontFamilies.Default, bold: true, italic: false);
        var widest = lines.Max(l => TextLayout.Measure(l, metrics, size));
        if (widest > own.Width - 12 && widest > 0)
        {
            size *= (own.Width - 12) / widest;
        }

        var style = new TextStyle(FontFamilies.Default, size, stamp.Color, Bold: true);
        var face = fonts.Face(style);
        var lineHeight = size * 1.15;
        var top = (own.Height - (lineHeight * lines.Length)) / 2;
        var ascent = (double)metrics.Ascender / metrics.UnitsPerEm * size;
        var descent = (double)-metrics.Descender / metrics.UnitsPerEm * size;
        for (var i = 0; i < lines.Length; i++)
        {
            var width = TextLayout.Measure(lines[i], metrics, size);
            var baseline = top + (i * lineHeight) + ((lineHeight + ascent - descent) / 2);
            if (OverlayDrawing.Run(face, lines[i], size, new Point((own.Width - width) / 2, baseline), pixelsPerDip) is { } run)
            {
                context.DrawGlyphRun(brush, run);
            }
        }
    }

    private static Point Screen(PageCoordinateMapper mapper, PdfPoint point)
    {
        var shown = mapper.PdfToScreen(point);
        return new Point(shown.X, shown.Y);
    }

    private static StreamGeometry Polygon(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: closed, isClosed: closed);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
