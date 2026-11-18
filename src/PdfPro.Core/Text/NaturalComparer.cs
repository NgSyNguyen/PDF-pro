using System.Globalization;

namespace PdfPro.Core.Text;

/// <summary>Compares file names the way Explorer sorts them: digit runs by value ("2" before "10").</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                var byValue = numberX.Length != numberY.Length ? numberX.Length.CompareTo(numberY.Length) : string.CompareOrdinal(numberX, numberY);
                if (byValue != 0)
                {
                    return byValue;
                }
            }
            else
            {
                var byText = string.Compare(x[i].ToString(), y[j].ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (byText != 0)
                {
                    return byText;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
