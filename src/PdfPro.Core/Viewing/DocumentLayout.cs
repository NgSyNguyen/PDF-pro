using PdfPro.Core.Geometry;

namespace PdfPro.Core.Viewing;

/// <summary>How pages are arranged in the continuous view.</summary>
public enum PageLayoutMode
{
    /// <summary>One page per row.</summary>
    Single,

    /// <summary>Two pages side by side: 1-2, 3-4…</summary>
    Facing,

    /// <summary>Like a book: the first page alone on the right, then 2-3, 4-5…</summary>
    FacingWithCover,
}

/// <summary>Spacing in view units; it does not scale with the zoom.</summary>
public sealed record LayoutSpacing(double Margin = 16, double RowGap = 12, double ColumnGap = 12)
{
    public static LayoutSpacing Default { get; } = new();
}

/// <summary>One row of the layout (one page, or a facing pair).</summary>
public readonly record struct LayoutRow(int FirstPage, int PageCount, double Top, double Height)
{
    public double Bottom => Top + Height;

    public int LastPage => FirstPage + PageCount - 1;
}

/// <summary>
/// Positions of every page of a document in one continuous, vertically scrolling surface. Pure and immutable:
/// a new layout is computed whenever the zoom, the mode, the page sizes or the viewport width change.
/// All rectangles are in view units relative to the top-left of the whole surface.
/// </summary>
public sealed class DocumentLayout
{
    /// <summary>View units per point at 100 % zoom (WPF: 96 DIP per inch, PDF: 72 points per inch).</summary>
    public const double UnitsPerPointAt100Percent = 96.0 / 72.0;

    private readonly ViewRect[] _pages;
    private readonly LayoutRow[] _rows;
    private readonly int[] _rowOfPage;

    private DocumentLayout(ViewRect[] pages, LayoutRow[] rows, int[] rowOfPage, ViewSize extent, double zoom, PageLayoutMode mode, LayoutSpacing spacing)
    {
        _pages = pages;
        _rows = rows;
        _rowOfPage = rowOfPage;
        Extent = extent;
        Zoom = zoom;
        Mode = mode;
        Spacing = spacing;
    }

    public ViewSize Extent { get; }

    public double Zoom { get; }

    /// <summary>View units per PDF point.</summary>
    public double Scale => Zoom * UnitsPerPointAt100Percent;

    public PageLayoutMode Mode { get; }

    public LayoutSpacing Spacing { get; }

    public int PageCount => _pages.Length;

    public IReadOnlyList<LayoutRow> Rows => _rows;

    /// <param name="pageSizes">Displayed page sizes in points (already rotated).</param>
    /// <param name="zoom">1.0 = 100 %.</param>
    /// <param name="mode">Single column or facing pages.</param>
    /// <param name="viewportWidth">Pages narrower than the viewport are centred in it.</param>
    /// <param name="spacing">Margins and gaps; <see cref="LayoutSpacing.Default"/> when null.</param>
    public static DocumentLayout Create(IReadOnlyList<PdfSize> pageSizes, double zoom, PageLayoutMode mode, double viewportWidth, LayoutSpacing? spacing = null)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "The zoom must be a positive number.");
        }

        spacing ??= LayoutSpacing.Default;
        viewportWidth = double.IsFinite(viewportWidth) ? Math.Max(0, viewportWidth) : 0;
        var scale = zoom * UnitsPerPointAt100Percent;
        var sizes = pageSizes.Select(s => new ViewSize(Math.Max(s.Width, 1) * scale, Math.Max(s.Height, 1) * scale)).ToArray();
        var rowsPages = GroupIntoRows(sizes.Length, mode);

        // Width: single mode centres each page; facing modes centre the spine, so both halves need the same room.
        double contentWidth;
        if (mode == PageLayoutMode.Single)
        {
            contentWidth = sizes.Length == 0 ? 0 : sizes.Max(s => s.Width);
        }
        else
        {
            var half = sizes.Length == 0 ? 0 : sizes.Max(s => s.Width);
            contentWidth = (2 * half) + spacing.ColumnGap;
        }

        var extentWidth = Math.Max(viewportWidth, contentWidth + (2 * spacing.Margin));
        var center = extentWidth / 2;

        var pages = new ViewRect[sizes.Length];
        var rowOfPage = new int[sizes.Length];
        var rows = new LayoutRow[rowsPages.Count];
        var top = spacing.Margin;
        for (var r = 0; r < rowsPages.Count; r++)
        {
            var (first, count, slot) = rowsPages[r];
            var height = 0.0;
            for (var i = first; i < first + count; i++)
            {
                height = Math.Max(height, sizes[i].Height);
            }

            for (var i = first; i < first + count; i++)
            {
                var size = sizes[i];
                double x;
                if (mode == PageLayoutMode.Single)
                {
                    x = center - (size.Width / 2);
                }
                else
                {
                    // Left slot: right edge on the spine; right slot: left edge on the spine.
                    var isLeft = count == 2 ? i == first : slot == Slot.Left;
                    x = isLeft ? center - (spacing.ColumnGap / 2) - size.Width : center + (spacing.ColumnGap / 2);
                }

                pages[i] = new ViewRect(x, top + ((height - size.Height) / 2), size.Width, size.Height);
                rowOfPage[i] = r;
            }

            rows[r] = new LayoutRow(first, count, top, height);
            top += height + spacing.RowGap;
        }

        var extentHeight = rows.Length == 0 ? 2 * spacing.Margin : rows[^1].Bottom + spacing.Margin;
        return new DocumentLayout(pages, rows, rowOfPage, new ViewSize(extentWidth, extentHeight), zoom, mode, spacing);
    }

    public ViewRect GetPageBounds(int pageIndex)
    {
        CheckPage(pageIndex);
        return _pages[pageIndex];
    }

    public int GetRowOfPage(int pageIndex)
    {
        CheckPage(pageIndex);
        return _rowOfPage[pageIndex];
    }

    /// <summary>First and last page of the rows that intersect <paramref name="viewport"/> (inclusive), or null.</summary>
    public (int First, int Last)? GetVisiblePages(ViewRect viewport)
    {
        if (_rows.Length == 0 || viewport.Height <= 0)
        {
            return null;
        }

        var firstRow = FirstRowEndingBelow(viewport.Y);
        if (firstRow >= _rows.Length || _rows[firstRow].Top > viewport.Bottom)
        {
            return null;
        }

        var lastRow = firstRow;
        while (lastRow + 1 < _rows.Length && _rows[lastRow + 1].Top <= viewport.Bottom)
        {
            lastRow++;
        }

        return (_rows[firstRow].FirstPage, _rows[lastRow].LastPage);
    }

    /// <summary>The page under a surface point, or null in margins and gaps.</summary>
    public int? GetPageAt(ViewPoint point)
    {
        var row = RowAt(point.Y);
        if (row is null)
        {
            return null;
        }

        var r = _rows[row.Value];
        for (var i = r.FirstPage; i <= r.LastPage; i++)
        {
            if (_pages[i].Contains(point))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The page nearest to a surface point (the page itself when the point is on it).</summary>
    public int GetNearestPage(ViewPoint point)
    {
        if (_pages.Length == 0)
        {
            throw new InvalidOperationException("The layout has no pages.");
        }

        var rowIndex = Math.Clamp(FirstRowEndingBelow(point.Y), 0, _rows.Length - 1);
        var best = -1;
        var bestDistance = double.MaxValue;
        foreach (var candidateRow in new[] { rowIndex - 1, rowIndex, rowIndex + 1 })
        {
            if (candidateRow < 0 || candidateRow >= _rows.Length)
            {
                continue;
            }

            var r = _rows[candidateRow];
            for (var i = r.FirstPage; i <= r.LastPage; i++)
            {
                var distance = DistanceSquared(_pages[i], point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The page the user is looking at: the one with the largest visible area, or the nearest page to the
    /// viewport centre when only gaps are visible. Null for an empty document.
    /// </summary>
    public int? GetCurrentPage(ViewRect viewport)
    {
        if (_pages.Length == 0)
        {
            return null;
        }

        var visible = GetVisiblePages(viewport);
        if (visible is { } range)
        {
            var best = -1;
            var bestArea = 0.0;
            for (var i = range.First; i <= range.Last; i++)
            {
                var overlap = ViewRect.Intersect(_pages[i], viewport);
                var area = overlap.Width * overlap.Height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = i;
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return GetNearestPage(new ViewPoint(viewport.X + (viewport.Width / 2), viewport.Y + (viewport.Height / 2)));
    }

    /// <summary>The largest valid scroll offset for a viewport of the given size.</summary>
    public ViewPoint MaxOffset(ViewSize viewport)
        => new(Math.Max(0, Extent.Width - viewport.Width), Math.Max(0, Extent.Height - viewport.Height));

    public ViewPoint ClampOffset(ViewPoint offset, ViewSize viewport)
    {
        var max = MaxOffset(viewport);
        return new ViewPoint(Math.Clamp(offset.X, 0, max.X), Math.Clamp(offset.Y, 0, max.Y));
    }

    /// <summary>
    /// Scroll offset that shows <paramref name="pointInPage"/> (view units from the page's top-left) at the top
    /// of the viewport, a gap below the top edge. Horizontally it keeps <paramref name="currentOffsetX"/> while the
    /// page stays on screen; with null (or when the page would be off screen) the page's row is centred.
    /// </summary>
    public ViewPoint OffsetToShow(int pageIndex, ViewPoint pointInPage, double? currentOffsetX, ViewSize viewport)
    {
        var page = GetPageBounds(pageIndex);

        // Going to the top of a first-row page shows the whole document top margin.
        var y = _rowOfPage[pageIndex] == 0 && pointInPage.Y <= 0 ? 0 : page.Y + pointInPage.Y - (Spacing.RowGap / 2);
        double x;
        if (currentOffsetX is { } current && page.Right >= current && page.X <= current + viewport.Width)
        {
            x = current;
        }
        else
        {
            var row = _rows[_rowOfPage[pageIndex]];
            var left = _pages[row.FirstPage].X;
            var right = _pages[row.LastPage].Right;
            x = (left + right - viewport.Width) / 2;
        }

        return ClampOffset(new ViewPoint(x, y), viewport);
    }

    /// <summary>
    /// After a zoom (or mode) change from <paramref name="before"/> to <paramref name="after"/>, the scroll offset
    /// that keeps the same spot of the same page under <paramref name="anchorInViewport"/> (e.g. the mouse).
    /// The result is clamped to the new extent.
    /// </summary>
    public static ViewPoint AnchoredOffset(DocumentLayout before, DocumentLayout after, ViewPoint offsetBefore, ViewPoint anchorInViewport, ViewSize viewport)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.PageCount == 0 || before.PageCount != after.PageCount)
        {
            return after.ClampOffset(offsetBefore, viewport);
        }

        var surfacePoint = offsetBefore + anchorInViewport;
        var page = before.GetPageAt(surfacePoint) ?? before.GetNearestPage(surfacePoint);
        var oldBounds = before._pages[page];
        var newBounds = after._pages[page];

        // Relative position inside the page; may lie outside [0, 1] when the anchor is in a margin.
        var relativeX = (surfacePoint.X - oldBounds.X) / oldBounds.Width;
        var relativeY = (surfacePoint.Y - oldBounds.Y) / oldBounds.Height;
        var newSurfacePoint = new ViewPoint(newBounds.X + (relativeX * newBounds.Width), newBounds.Y + (relativeY * newBounds.Height));
        return after.ClampOffset(newSurfacePoint - anchorInViewport, viewport);
    }

    private enum Slot
    {
        Left,
        Right,
    }

    private static List<(int First, int Count, Slot Slot)> GroupIntoRows(int pageCount, PageLayoutMode mode)
    {
        var rows = new List<(int, int, Slot)>();
        var index = 0;
        if (mode == PageLayoutMode.FacingWithCover && pageCount > 0)
        {
            rows.Add((0, 1, Slot.Right));
            index = 1;
        }

        while (index < pageCount)
        {
            if (mode == PageLayoutMode.Single)
            {
                rows.Add((index, 1, Slot.Left));
                index++;
            }
            else
            {
                var count = Math.Min(2, pageCount - index);
                rows.Add((index, count, Slot.Left));
                index += count;
            }
        }

        return rows;
    }

    private static double DistanceSquared(ViewRect rect, ViewPoint point)
    {
        var dx = point.X < rect.X ? rect.X - point.X : point.X > rect.Right ? point.X - rect.Right : 0;
        var dy = point.Y < rect.Y ? rect.Y - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Index of the first row whose bottom is at or below <paramref name="y"/> (binary search).</summary>
    private int FirstRowEndingBelow(double y)
    {
        int low = 0, high = _rows.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_rows[mid].Bottom < y)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int? RowAt(double y)
    {
        var row = FirstRowEndingBelow(y);
        return row < _rows.Length && _rows[row].Top <= y ? row : null;
    }

    private void CheckPage(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)_pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The layout has {_pages.Length} pages.");
        }
    }
}
