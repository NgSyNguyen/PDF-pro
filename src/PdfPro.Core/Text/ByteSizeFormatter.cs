namespace PdfPro.Core.Text;

/// <summary>Human-readable sizes in binary units: "512 B", "198 KB", "1,5 MB" (with the culture's decimal separator).</summary>
public static class ByteSizeFormatter
{
    private const double Step = 1024;
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>One decimal below 10 units ("1,5 MB"), none above ("198 KB"); plain bytes under 1 KB.</summary>
    public static string Format(long bytes, IFormatProvider? culture)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < Step)
        {
            return bytes.ToString(culture) + " B";
        }

        double value = bytes;
        var unit = -1;
        while (value >= Step && unit < Units.Length - 1)
        {
            value /= Step;
            unit++;
        }

        return value.ToString(value < 10 ? "0.#" : "0", culture) + " " + Units[unit];
    }
}
