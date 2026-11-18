using System.Globalization;
using System.Text;
using PdfPro.Core.Stamping;

namespace PdfPro.Core.Structure;

/// <summary>How the pages of a label range are numbered (ISO 32000 12.4.2, /S).</summary>
public enum PageLabelStyle
{
    /// <summary>No number: the prefix alone ("Bìa").</summary>
    None,

    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>A … Z, AA … ZZ.</summary>
    UpperLetters,

    /// <summary>a … z, aa … zz.</summary>
    LowerLetters,
}

/// <summary>A range of page labels: from <paramref name="FirstPage"/> (0-based) to the next range, numbered from <paramref name="Start"/>.</summary>
public sealed record PageLabelRange(int FirstPage, PageLabelStyle Style = PageLabelStyle.Decimal, string? Prefix = null, int Start = 1);

/// <summary>Page labels ("i, ii, iii" for the front matter, "1, 2…" for the body) as a viewer shows them.</summary>
public static class PageLabels
{
    /// <summary>The label of a page, or null when no range covers it (its number is shown instead).</summary>
    public static string? Of(IReadOnlyList<PageLabelRange> ranges, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var range = ranges.Where(r => r.FirstPage <= pageIndex).MaxBy(r => r.FirstPage);
        if (range is null)
        {
            return null;
        }

        var number = range.Start + (pageIndex - range.FirstPage);
        return (range.Prefix ?? string.Empty) + Number(number, range.Style);
    }

    /// <summary>The number in a style ("" for <see cref="PageLabelStyle.None"/>).</summary>
    public static string Number(int number, PageLabelStyle style) => style switch
    {
        PageLabelStyle.None => string.Empty,
        PageLabelStyle.UpperRoman => PageNumbering.ToRoman(number),
        PageLabelStyle.LowerRoman => PageNumbering.ToRoman(number).ToLowerInvariant(),
        PageLabelStyle.UpperLetters => Letters(number),
        PageLabelStyle.LowerLetters => Letters(number).ToLowerInvariant(),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The ranges made valid: sorted, one per first page, inside the document, the first one at page 0 when any.</summary>
    public static IReadOnlyList<PageLabelRange> Normalize(IReadOnlyList<PageLabelRange> ranges, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return [.. ranges
            .Where(r => r.FirstPage >= 0 && r.FirstPage < pageCount)
            .GroupBy(r => r.FirstPage)
            .Select(g => g.Last())
            .OrderBy(r => r.FirstPage)
            .Select(r => r with { Start = Math.Max(1, r.Start), Prefix = string.IsNullOrEmpty(r.Prefix) ? null : r.Prefix })];
    }

    /// <summary>"A" … "Z", then "AA" … "ZZ", "AAA" (12.4.2: the letter repeated).</summary>
    private static string Letters(int number)
    {
        if (number < 1)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var letter = (char)('A' + ((number - 1) % 26));
        return new StringBuilder().Append(letter, ((number - 1) / 26) + 1).ToString();
    }
}
