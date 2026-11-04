using PdfPro.Core.Geometry;

namespace PdfPro.Core.Annotations;

/// <summary>
/// Which annotation is under a point of the page (PDF user space), for selecting with the mouse: marked text by its
/// lines, strokes and lines by their path (with some slack, they are thin), closed or filled shapes by their area,
/// open ones by their outline, boxes, notes and stamps by their box.
/// </summary>
public static class AnnotationHitTest
{
    /// <summary>The topmost annotation under <paramref name="point"/>, or null.</summary>
    /// <param name="annotations">In drawing order (the last one is on top).</param>
    /// <param name="point">The point, in the page's PDF user space.</param>
    /// <param name="tolerance">Slack in points around thin parts (a few pixels at the current zoom).</param>
    public static AnnotationItem? At(IReadOnlyList<AnnotationItem> annotations, PdfPoint point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        for (var i = annotations.Count - 1; i >= 0; i--)
        {
            if (Contains(annotations[i], point, tolerance))
            {
                return annotations[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The topmost comment already in the file under <paramref name="point"/>, or null: marked text by its lines, the
    /// others by their box. Answers, hidden ones and what is not a comment (links, fields, pop-ups) are not picked.
    /// </summary>
    /// <param name="annotations">In /Annots order (the last one is on top).</param>
    /// <param name="point">The point, in the page's PDF user space.</param>
    /// <param name="tolerance">Slack in points around them.</param>
    public static FileAnnotation? At(IReadOnlyList<FileAnnotation> annotations, PdfPoint point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        for (var i = annotations.Count - 1; i >= 0; i--)
        {
            var annotation = annotations[i];
            if (AnnotationGroups.Of(annotation.Kind) is null || annotation.IsReply || annotation.IsHidden)
            {
                continue;
            }

            var hit = annotation.Quads.Count > 0
                ? annotation.Quads.Any(q => Inflate(q.Bounds, tolerance).Contains(point))
                : Inflate(annotation.Rect, tolerance).Contains(point);
            if (hit)
            {
                return annotation;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="point"/> (PDF user space) is on the annotation.</summary>
    public static bool Contains(AnnotationItem annotation, PdfPoint point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        return annotation switch
        {
            MarkupAnnotation markup => markup.Quads.Any(q => InPolygon([q.UpperLeft, q.UpperRight, q.LowerRight, q.LowerLeft], point)),
            InkAnnotation ink => ink.Strokes.Any(s => NearPath(s, closed: false, point, (ink.Width / 2) + tolerance)),
            LineAnnotation line => NearPath([line.Start, line.End], closed: false, point, (line.Width / 2) + tolerance),
            PolyAnnotation { Closed: true } polygon => InPolygon(polygon.Vertices, point) || NearPath(polygon.Vertices, closed: true, point, (polygon.Width / 2) + tolerance),
            PolyAnnotation polyline => NearPath(polyline.Vertices, closed: false, point, (polyline.Width / 2) + tolerance),
            ShapeAnnotation { Fill: null } shape => NearOutline(shape, point, (shape.Width / 2) + tolerance),
            FreeTextAnnotation freeText => Inside(freeText.Box, point, tolerance) || (freeText.Callout.Count > 1 && NearPath(freeText.Callout, closed: false, point, 1 + tolerance)),
            _ => Inside(annotation.Bounds, point, tolerance),
        };
    }

    /// <summary>Distance from a point to a segment.</summary>
    public static double DistanceToSegment(PdfPoint point, PdfPoint a, PdfPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        var (x, y) = (a.X + (t * dx), a.Y + (t * dy));
        return Math.Sqrt(((point.X - x) * (point.X - x)) + ((point.Y - y) * (point.Y - y)));
    }

    private static bool Inside(PdfRect rect, PdfPoint point, double tolerance)
        => point.X >= rect.Left - tolerance && point.X <= rect.Right + tolerance && point.Y >= rect.Bottom - tolerance && point.Y <= rect.Top + tolerance;

    /// <summary>An unfilled rectangle or ellipse is picked by its outline only, so what is inside it stays reachable.</summary>
    private static bool NearOutline(ShapeAnnotation shape, PdfPoint point, double slack)
    {
        var box = shape.Box;
        if (shape.Shape == NoteShape.Square)
        {
            PdfPoint[] corners = [new(box.Left, box.Bottom), new(box.Right, box.Bottom), new(box.Right, box.Top), new(box.Left, box.Top)];
            return NearPath(corners, closed: true, point, slack);
        }

        // Distance to an ellipse, approximated on the normalized circle (exact for circles, close enough for picking).
        var (cx, cy) = ((box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2);
        var (rx, ry) = (Math.Max(box.Width / 2, 1e-6), Math.Max(box.Height / 2, 1e-6));
        var (nx, ny) = ((point.X - cx) / rx, (point.Y - cy) / ry);
        var radial = Math.Sqrt((nx * nx) + (ny * ny));
        return Math.Abs(radial - 1) * Math.Min(rx, ry) <= slack;
    }

    private static bool NearPath(IReadOnlyList<PdfPoint> points, bool closed, PdfPoint point, double slack)
    {
        if (points.Count == 1)
        {
            return DistanceToSegment(point, points[0], points[0]) <= slack;
        }

        var segments = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            if (DistanceToSegment(point, points[i], points[(i + 1) % points.Count]) <= slack)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    private static bool InPolygon(IReadOnlyList<PdfPoint> polygon, PdfPoint point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static PdfRect Inflate(PdfRect rect, double by) => new(rect.Left - by, rect.Bottom - by, rect.Right + by, rect.Top + by);
}
