using System.Windows;

namespace PdfPro.App.Controls;

/// <summary>
/// Geometry of a grid of equal tiles laid out left to right, then top to bottom, in as many columns as fit the width;
/// the grid is centred when there is room left. Positions are in content coordinates (before scrolling). Pure, so the
/// virtualizing panel's layout, rubber band and scrolling math can be unit tested.
/// </summary>
public readonly record struct TileLayout
{
    /// <param name="count">Number of tiles.</param>
    /// <param name="tileWidth">Width of a tile, including its margins.</param>
    /// <param name="tileHeight">Height of a tile, including its margins.</param>
    /// <param name="availableWidth">Width to lay the tiles out in.</param>
    public TileLayout(int count, double tileWidth, double tileHeight, double availableWidth)
    {
        if (!(tileWidth > 0) || !(tileHeight > 0) || double.IsInfinity(tileWidth) || double.IsInfinity(tileHeight))
        {
            throw new ArgumentOutOfRangeException(nameof(tileWidth), "Tiles need a finite positive size.");
        }

        Count = Math.Max(0, count);
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        var width = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : tileWidth;
        Columns = Math.Max(1, (int)Math.Floor((width + 0.001) / tileWidth));
        Rows = (Count + Columns - 1) / Columns;
        Left = Math.Max(0, (width - (Columns * tileWidth)) / 2);
        Width = Math.Max(width, Columns * tileWidth);
    }

    public int Count { get; }

    public double TileWidth { get; }

    public double TileHeight { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Space left of the first column (the grid is centred).</summary>
    public double Left { get; }

    /// <summary>Width of the content.</summary>
    public double Width { get; }

    /// <summary>Height of the content.</summary>
    public double Height => Rows * TileHeight;

    public int RowOf(int index) => index / Columns;

    public Rect TileRect(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new Rect(Left + (index % Columns * TileWidth), RowOf(index) * TileHeight, TileWidth, TileHeight);
    }

    /// <summary>
    /// The tiles of the rows that cross [<paramref name="top"/>, <paramref name="top"/> + <paramref name="height"/>),
    /// plus <paramref name="extraRows"/> rows above and below. Empty (Last &lt; First) when there are no tiles.
    /// </summary>
    public (int First, int Last) RangeFor(double top, double height, int extraRows = 0)
    {
        if (Count == 0)
        {
            return (0, -1);
        }

        var firstRow = Math.Clamp((int)Math.Floor(Math.Max(0, top) / TileHeight) - extraRows, 0, Rows - 1);
        var lastRow = Math.Clamp((int)Math.Ceiling(Math.Max(0, top + Math.Max(0, height)) / TileHeight) - 1 + extraRows, firstRow, Rows - 1);
        return (firstRow * Columns, Math.Min(Count - 1, ((lastRow + 1) * Columns) - 1));
    }

    /// <summary>The tiles a rectangle (a rubber band) touches, in order.</summary>
    public IEnumerable<int> IndicesIn(Rect area)
    {
        if (Count == 0 || area.IsEmpty || area.Width <= 0 || area.Height <= 0)
        {
            yield break;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(area.Top / TileHeight));
        var lastRow = Math.Min(Rows - 1, (int)Math.Floor(area.Bottom / TileHeight));
        var firstColumn = Math.Max(0, (int)Math.Floor((area.Left - Left) / TileWidth));
        var lastColumn = Math.Min(Columns - 1, (int)Math.Floor((area.Right - Left) / TileWidth));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var index = (row * Columns) + column;
                if (index < Count && TileRect(index).IntersectsWith(area))
                {
                    yield return index;
                }
            }
        }
    }

    /// <summary>The offset that shows the whole tile <paramref name="index"/> while moving as little as possible.</summary>
    public double OffsetToShow(int index, double currentOffset, double viewportHeight)
    {
        var tile = TileRect(index);
        if (tile.Top < currentOffset || viewportHeight < tile.Height)
        {
            return tile.Top;
        }

        return tile.Bottom > currentOffset + viewportHeight ? tile.Bottom - viewportHeight : currentOffset;
    }
}
