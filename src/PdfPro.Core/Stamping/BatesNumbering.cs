using System.Globalization;

namespace PdfPro.Core.Stamping;

/// <summary>A Bates label: prefix, zero-padded number, suffix ("HS-000123-A").</summary>
/// <param name="Digits">Minimum number of digits (1–15); larger numbers simply have more digits.</param>
public sealed record BatesFormat(string Prefix, int Digits, string Suffix)
{
    public const int MaxDigits = 15;

    public string Label(long number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        return Prefix + number.ToString("D" + Math.Clamp(Digits, 1, MaxDigits).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + Suffix;
    }

    /// <summary>True when <paramref name="number"/> needs more digits than <see cref="Digits"/>.</summary>
    public bool Overflows(long number) => number.ToString(CultureInfo.InvariantCulture).Length > Digits;
}

/// <summary>The Bates numbers of one file: <see cref="First"/> to <see cref="Last"/>, one per page.</summary>
public sealed record BatesRange(int FileIndex, long First, long Last)
{
    public long NumberOf(int pageIndex) => First + pageIndex;
}

/// <summary>Bates numbering across several files: one number per page, running on from file to file without gaps or repeats.</summary>
public static class BatesNumbering
{
    public static IReadOnlyList<BatesRange> Plan(IReadOnlyList<int> pageCounts, long startNumber)
    {
        ArgumentNullException.ThrowIfNull(pageCounts);
        ArgumentOutOfRangeException.ThrowIfNegative(startNumber);
        var ranges = new List<BatesRange>(pageCounts.Count);
        var next = startNumber;
        for (var i = 0; i < pageCounts.Count; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCounts[i]);
            ranges.Add(new BatesRange(i, next, next + pageCounts[i] - 1));
            next += pageCounts[i];
        }

        return ranges;
    }
}
