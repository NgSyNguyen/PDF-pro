namespace PdfPro.Core.Layout;

/// <summary>Which edge the folded booklet is bound on.</summary>
public enum BookletBinding
{
    /// <summary>Read left to right (Vietnamese, English).</summary>
    Left,

    /// <summary>Read right to left.</summary>
    Right,
}

/// <summary>One printed side of a booklet sheet: the pages left and right of the fold (-1 = blank).</summary>
public readonly record struct BookletSide(int Left, int Right);

/// <summary>
/// Saddle-stitch booklet imposition: sheets printed on both sides (flip on the short edge), stacked and folded in the
/// middle. The page count is padded with blank pages at the end to a multiple of 4.
/// </summary>
public static class BookletLayout
{
    /// <summary>Pages the booklet has once padded (a multiple of 4).</summary>
    public static int PaddedPageCount(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        return (pageCount + 3) / 4 * 4;
    }

    /// <summary>
    /// The sides in printing order (front of sheet 1, back of sheet 1, front of sheet 2…). For 12 pages, bound left:
    /// [12|1], [2|11], [10|3], [4|9], [8|5], [6|7] (1-based).
    /// </summary>
    public static IReadOnlyList<BookletSide> Sides(int pageCount, BookletBinding binding = BookletBinding.Left)
    {
        var padded = PaddedPageCount(pageCount);
        var sides = new BookletSide[padded / 2];
        for (var k = 0; k < sides.Length; k++)
        {
            var (left, right) = k % 2 == 0 ? (padded - 1 - k, k) : (k, padded - 1 - k);
            if (binding == BookletBinding.Right)
            {
                (left, right) = (right, left);
            }

            sides[k] = new BookletSide(left < pageCount ? left : -1, right < pageCount ? right : -1);
        }

        return sides;
    }
}
