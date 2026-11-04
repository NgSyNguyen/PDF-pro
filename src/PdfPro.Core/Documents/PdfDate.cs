using System.Globalization;

namespace PdfPro.Core.Documents;

/// <summary>
/// PDF date strings (ISO 32000-1 §7.9.4): <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, where everything after the year is
/// optional and O is +, - or Z. Real-world files are sloppy, so parsing is lenient but never throws.
/// </summary>
public static class PdfDate
{
    /// <summary>The full form, "D:20261027153000+07'00'" (UTC as "Z").</summary>
    public static string Format(DateTimeOffset value)
    {
        var date = value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (value.Offset == TimeSpan.Zero)
        {
            return $"D:{date}Z";
        }

        var offset = value.Offset.Duration();
        var sign = value.Offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture, $"D:{date}{sign}{offset.Hours:00}'{offset.Minutes:00}'");
    }

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var position = 0;
        if (!TryReadNumber(text, ref position, 4, out var year) || year < 1)
        {
            return false;
        }

        TryReadNumber(text, ref position, 2, out var month);
        TryReadNumber(text, ref position, 2, out var day);
        TryReadNumber(text, ref position, 2, out var hour);
        TryReadNumber(text, ref position, 2, out var minute);
        TryReadNumber(text, ref position, 2, out var second);

        month = month == 0 ? 1 : month;
        day = day == 0 ? 1 : day;
        if (month > 12 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var offset = TimeSpan.Zero;
        if (position < text.Length)
        {
            var sign = text[position];
            if (sign is '+' or '-')
            {
                position++;
                TryReadNumber(text, ref position, 2, out var offsetHours);
                if (position < text.Length && text[position] == '\'')
                {
                    position++;
                }

                TryReadNumber(text, ref position, 2, out var offsetMinutes);
                if (position < text.Length && text[position] == '\'')
                {
                    position++;
                }

                if (position < text.Length || offsetHours > 14 || offsetMinutes > 59)
                {
                    return false;
                }

                offset = new TimeSpan(offsetHours, offsetMinutes, 0);
                if (sign == '-')
                {
                    offset = -offset;
                }
            }
            else if (sign is not 'Z' and not 'z')
            {
                return false;
            }
        }

        result = new DateTimeOffset(year, month, day, hour, minute, second, offset);
        return true;
    }

    /// <summary>Reads a fixed-width number; a missing optional component leaves <paramref name="value"/> at 0.</summary>
    private static bool TryReadNumber(string text, ref int position, int digits, out int value)
    {
        if (position + digits > text.Length
            || !int.TryParse(text.AsSpan(position, digits), NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        position += digits;
        return true;
    }
}
