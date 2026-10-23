using System.Globalization;
using System.Text;

namespace PdfPro.Core.Stamping;

public enum PageNumberStyle
{
    /// <summary>1, 2, 3.</summary>
    Arabic,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>I, II, III.</summary>
    UpperRoman,
}

/// <summary>
/// Which pages get a number and which: numbering starts on page index <c>firstPage</c> with <c>startNumber</c> and goes
/// up by one per page; pages before it get none.
/// </summary>
public static class PageNumbering
{
    /// <summary>The number printed on <paramref name="pageIndex"/>, or null when the page is not numbered.</summary>
    public static int? NumberOf(int pageIndex, int firstPage, int startNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(firstPage);
        return pageIndex < firstPage ? null : startNumber + (pageIndex - firstPage);
    }

    /// <summary>The last number printed in a document of <paramref name="pageCount"/> pages ("{pages}").</summary>
    public static int LastNumber(int pageCount, int firstPage, int startNumber)
        => startNumber + Math.Max(0, pageCount - 1 - firstPage);

    public static string Format(int number, PageNumberStyle style) => style switch
    {
        PageNumberStyle.LowerRoman => ToRoman(number).ToLowerInvariant(),
        PageNumberStyle.UpperRoman => ToRoman(number),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Roman numerals for 1–3999; other numbers are written in Arabic digits.</summary>
    public static string ToRoman(int number)
    {
        if (number is < 1 or > 3999)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var roman = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (number >= values[i])
            {
                roman.Append(symbols[i]);
                number -= values[i];
            }
        }

        return roman.ToString();
    }
}
