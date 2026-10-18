namespace PdfPro.Core.Printing;

/// <summary>Turns the print dialog's choice into the 0-based page indices to print.</summary>
public static class PrintPageRange
{
    /// <summary>Every page of the document.</summary>
    public static IReadOnlyList<int> All(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        return Enumerable.Range(0, pageCount).ToArray();
    }

    /// <summary>
    /// Pages <paramref name="firstPage"/> to <paramref name="lastPage"/> (1-based, as typed by the user), clamped
    /// to the document; the bounds may be given in either order.
    /// </summary>
    public static IReadOnlyList<int> Between(int firstPage, int lastPage, int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        if (pageCount == 0)
        {
            return [];
        }

        var from = Math.Clamp(Math.Min(firstPage, lastPage), 1, pageCount);
        var to = Math.Clamp(Math.Max(firstPage, lastPage), 1, pageCount);
        return Enumerable.Range(from - 1, to - from + 1).ToArray();
    }
}
