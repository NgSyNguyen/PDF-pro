using PdfPro.Core.Geometry;

namespace PdfPro.Core.Overlay;

/// <summary>
/// An alignment guide drawn while snapping: a vertical line x = <see cref="Position"/> (or a horizontal line
/// y = <see cref="Position"/>) from <see cref="From"/> to <see cref="To"/> along the other axis, in view space.
/// </summary>
public sealed record SnapGuide(bool IsVertical, double Position, double From, double To);

/// <summary>The correction to apply to a move and the guides to show for it.</summary>
public sealed record SnapResult(double DeltaX, double DeltaY, IReadOnlyList<SnapGuide> Guides)
{
    public static SnapResult None { get; } = new(0, 0, []);
}

/// <summary>
/// Snapping of moved or resized objects to the page's edges and centre lines and to the edges and centres of the other
/// objects on the page, within a threshold in view units (so it feels the same at every zoom).
/// </summary>
public static class OverlaySnapping
{
    /// <summary>Default distance, in view units, within which an edge snaps.</summary>
    public const double DefaultThreshold = 6;

    /// <summary>
    /// Snaps a moving selection whose bounds are <paramref name="moving"/> (after the raw move): its left, centre and right
    /// against vertical lines, its top, middle and bottom against horizontal lines.
    /// </summary>
    public static SnapResult SnapMove(ViewRect moving, ViewRect page, IReadOnlyList<ViewRect> others, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(others);
        var targets = others.Prepend(page).ToArray();
        var (dx, vertical) = Best([moving.X, Center(moving).X, moving.Right], targets, isVertical: true, threshold);
        var (dy, horizontal) = Best([moving.Y, Center(moving).Y, moving.Bottom], targets, isVertical: false, threshold);
        var snapped = new ViewRect(moving.X + dx, moving.Y + dy, moving.Width, moving.Height);
        return new SnapResult(dx, dy, [.. Guides(snapped, vertical, targets, true), .. Guides(snapped, horizontal, targets, false)]);
    }

    /// <summary>
    /// Snaps one dragged edge: <paramref name="value"/> is its x (vertical edge) or y (horizontal edge). Returns the
    /// corrected value and the guide, if any.
    /// </summary>
    public static (double Value, SnapGuide? Guide) SnapEdge(double value, bool isVertical, ViewRect page, IReadOnlyList<ViewRect> others, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(others);
        var targets = others.Prepend(page).ToArray();
        var (delta, line) = Best([value], targets, isVertical, threshold);
        if (line is not { } position)
        {
            return (value, null);
        }

        var spans = targets.Where(t => Lines(t, isVertical).Any(l => Math.Abs(l - position) < 1e-6)).ToArray();
        var from = spans.Min(t => isVertical ? t.Y : t.X);
        var to = spans.Max(t => isVertical ? t.Bottom : t.Right);
        return (value + delta, new SnapGuide(isVertical, position, from, to));
    }

    private static (double Delta, double? Line) Best(double[] edges, ViewRect[] targets, bool isVertical, double threshold)
    {
        var bestDelta = 0.0;
        double? bestLine = null;
        foreach (var target in targets)
        {
            foreach (var line in Lines(target, isVertical))
            {
                foreach (var edge in edges)
                {
                    var delta = line - edge;
                    if (Math.Abs(delta) <= threshold && (bestLine is null || Math.Abs(delta) < Math.Abs(bestDelta)))
                    {
                        (bestDelta, bestLine) = (delta, line);
                    }
                }
            }
        }

        return (bestDelta, bestLine);
    }

    private static IEnumerable<SnapGuide> Guides(ViewRect snapped, double? line, ViewRect[] targets, bool isVertical)
    {
        if (line is not { } position)
        {
            yield break;
        }

        // One guide through every target that has an edge or centre on the line, spanning them and the moved object.
        var spans = targets.Where(t => Lines(t, isVertical).Any(l => Math.Abs(l - position) < 1e-6)).Append(snapped).ToArray();
        var from = spans.Min(t => isVertical ? t.Y : t.X);
        var to = spans.Max(t => isVertical ? t.Bottom : t.Right);
        yield return new SnapGuide(isVertical, position, from, to);
    }

    private static double[] Lines(ViewRect rect, bool isVertical)
        => isVertical ? [rect.X, Center(rect).X, rect.Right] : [rect.Y, Center(rect).Y, rect.Bottom];

    private static ViewPoint Center(ViewRect rect) => new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
}
