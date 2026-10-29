namespace PdfPro.Core.Geometry;

/// <summary>A piece of a line left outside removed areas: its points in order, closed when nothing of it was cut.</summary>
public sealed record ClippedLine(IReadOnlyList<PdfPoint> Points, bool Closed);

/// <summary>
/// Cutting rectangles out of drawn lines and filled rectangles: what redaction keeps of a drawing that crosses an area.
/// Touching an edge is not crossing it.
/// </summary>
public static class RectClipping
{
    /// <summary>Pieces shorter than this (in the line's units) are dropped.</summary>
    private const double MinLength = 1e-6;

    /// <summary>
    /// The parts of a polyline that lie outside every rectangle, in order along the line. A closed polyline that is cut
    /// becomes open pieces (the one running through its first point is joined up); one that is not cut comes back closed.
    /// </summary>
    public static IReadOnlyList<ClippedLine> Outside(IReadOnlyList<PdfPoint> points, bool closed, IReadOnlyList<PdfRect> rects)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(rects);
        if (points.Count == 0)
        {
            return [];
        }

        var segments = new List<(PdfPoint From, PdfPoint To)>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            segments.Add((points[i], points[i + 1]));
        }

        if (closed && points.Count > 1 && points[^1] != points[0])
        {
            segments.Add((points[^1], points[0]));
        }

        if (segments.Count == 0)
        {
            return rects.Any(r => Inside(points[0], r)) ? [] : [new ClippedLine([points[0]], closed)];
        }

        var chains = new List<List<PdfPoint>>();
        List<PdfPoint>? current = null;
        var cut = false;
        var firstStartsAtZero = false;
        var lastEndsAtOne = false;
        for (var s = 0; s < segments.Count; s++)
        {
            var (from, to) = segments[s];
            var kept = Kept(from, to, rects);
            cut |= kept.Count != 1 || kept[0].Start > 0 || kept[0].End < 1;
            if (kept.Count == 0 || kept[0].Start > 0)
            {
                current = null;
            }

            foreach (var (start, end) in kept)
            {
                var a = At(from, to, start);
                var b = At(from, to, end);
                if (current is not null && start == 0)
                {
                    current.Add(b);
                }
                else
                {
                    current = [a, b];
                    chains.Add(current);
                    if (s == 0 && start == 0)
                    {
                        firstStartsAtZero = true;
                    }
                }

                if (end < 1)
                {
                    current = null;
                }
            }

            lastEndsAtOne = s == segments.Count - 1 && kept.Count > 0 && kept[^1].End >= 1;
        }

        if (!cut)
        {
            return [new ClippedLine([.. points], closed)];
        }

        // A closed line cut somewhere else than at its first point: the last piece runs on into the first one.
        if (closed && chains.Count > 1 && firstStartsAtZero && lastEndsAtOne)
        {
            var last = chains[^1];
            last.AddRange(chains[0].Skip(1));
            chains.RemoveAt(0);
        }

        return [.. chains.Where(c => Length(c) > MinLength).Select(c => new ClippedLine(c, false))];
    }

    /// <summary>What remains of <paramref name="rect"/> once every hole is taken out, as rectangles that do not overlap.</summary>
    public static IReadOnlyList<PdfRect> Subtract(PdfRect rect, IReadOnlyList<PdfRect> holes)
    {
        ArgumentNullException.ThrowIfNull(holes);
        var pieces = new List<PdfRect> { rect };
        foreach (var hole in holes)
        {
            var next = new List<PdfRect>();
            foreach (var piece in pieces)
            {
                var overlap = PdfRect.Intersect(piece, hole);
                if (overlap.IsEmpty)
                {
                    next.Add(piece);
                    continue;
                }

                // Full-height strips left and right of the hole, then what is above and below it in between.
                AddIfSized(next, new PdfRect(piece.Left, piece.Bottom, overlap.Left, piece.Top));
                AddIfSized(next, new PdfRect(overlap.Right, piece.Bottom, piece.Right, piece.Top));
                AddIfSized(next, new PdfRect(overlap.Left, piece.Bottom, overlap.Right, overlap.Bottom));
                AddIfSized(next, new PdfRect(overlap.Left, overlap.Top, overlap.Right, piece.Top));
            }

            pieces = next;
        }

        return pieces;
    }

    /// <summary>Whether two rectangles share some area (touching edges do not).</summary>
    public static bool Overlaps(PdfRect a, PdfRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    /// <summary>The parameter intervals of a segment outside every rectangle, in order.</summary>
    private static List<(double Start, double End)> Kept(PdfPoint from, PdfPoint to, IReadOnlyList<PdfRect> rects)
    {
        var kept = new List<(double Start, double End)> { (0, 1) };
        foreach (var rect in rects)
        {
            if (Inside(from, to, rect) is not { } inside)
            {
                continue;
            }

            var next = new List<(double Start, double End)>();
            foreach (var (start, end) in kept)
            {
                if (inside.End <= start || inside.Start >= end)
                {
                    next.Add((start, end));
                    continue;
                }

                if (inside.Start > start)
                {
                    next.Add((start, inside.Start));
                }

                if (inside.End < end)
                {
                    next.Add((inside.End, end));
                }
            }

            kept = next;
        }

        return kept;
    }

    /// <summary>The part of a segment strictly inside a rectangle (Liang–Barsky), or null.</summary>
    private static (double Start, double End)? Inside(PdfPoint from, PdfPoint to, PdfRect rect)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        double low = 0, high = 1;
        ReadOnlySpan<double> p = [-dx, dx, -dy, dy];
        ReadOnlySpan<double> q = [from.X - rect.Left, rect.Right - from.X, from.Y - rect.Bottom, rect.Top - from.Y];
        for (var i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] <= 0)
                {
                    return null; // parallel to this edge and not strictly inside it
                }

                continue;
            }

            var t = q[i] / p[i];
            if (p[i] < 0)
            {
                low = Math.Max(low, t);
            }
            else
            {
                high = Math.Min(high, t);
            }
        }

        return low < high ? (low, high) : null;
    }

    private static bool Inside(PdfPoint point, PdfRect rect)
        => point.X > rect.Left && point.X < rect.Right && point.Y > rect.Bottom && point.Y < rect.Top;

    private static PdfPoint At(PdfPoint from, PdfPoint to, double t)
        => t <= 0 ? from : t >= 1 ? to : new PdfPoint(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

    private static double Length(List<PdfPoint> chain)
    {
        var total = 0.0;
        for (var i = 0; i + 1 < chain.Count; i++)
        {
            total += Math.Sqrt(Math.Pow(chain[i + 1].X - chain[i].X, 2) + Math.Pow(chain[i + 1].Y - chain[i].Y, 2));
        }

        return total;
    }

    private static void AddIfSized(List<PdfRect> pieces, PdfRect piece)
    {
        if (!piece.IsEmpty)
        {
            pieces.Add(piece);
        }
    }
}
