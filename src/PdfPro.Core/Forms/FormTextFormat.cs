using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfPro.Core.Forms;

/// <summary>What a text field holds, when it is not free text.</summary>
public enum FormFormatKind
{
    None = 0,

    /// <summary>A number, shown with grouped thousands and a fixed number of decimals.</summary>
    Number = 1,

    /// <summary>A date, typed and shown in one pattern.</summary>
    Date = 2,
}

/// <summary>How a number is written: the thousands and the decimal separators (sepStyle of AFNumber_Format).</summary>
public enum NumberSeparators
{
    /// <summary>1,234.56</summary>
    CommaDot = 0,

    /// <summary>1.234,56 (Vietnamese)</summary>
    DotComma = 2,
}

/// <summary>
/// The format of a text field, written as the Acrobat JavaScript actions every major reader understands
/// (AFNumber_Keystroke / AFNumber_Format, AFDate_KeystrokeEx / AFDate_FormatEx). The value of a number field is kept
/// as a plain number ("1234.5"), shown formatted ("1.234,50"); the value of a date field is the date in its pattern.
/// </summary>
/// <param name="Kind">Number, date, or none.</param>
/// <param name="Decimals">Digits after the decimal separator of a number (0–10).</param>
/// <param name="Separators">The separators of a number.</param>
/// <param name="DatePattern">The pattern of a date, one of <see cref="DatePatterns"/> (Acrobat's letters).</param>
public sealed partial record FormTextFormat(
    FormFormatKind Kind,
    int Decimals = 0,
    NumberSeparators Separators = NumberSeparators.DotComma,
    string DatePattern = FormTextFormat.DefaultDatePattern)
{
    public const string DefaultDatePattern = "dd/mm/yyyy";

    public const int MaxDecimals = 10;

    /// <summary>The date patterns offered, in Acrobat's letters (m = month).</summary>
    public static IReadOnlyList<string> DatePatterns { get; } = ["dd/mm/yyyy", "mm/dd/yyyy", "yyyy-mm-dd", "dd.mm.yyyy", "dd-mm-yyyy"];

    public static FormTextFormat Number(int decimals = 0, NumberSeparators separators = NumberSeparators.DotComma)
        => new(FormFormatKind.Number, decimals, separators);

    public static FormTextFormat Date(string pattern = DefaultDatePattern) => new(FormFormatKind.Date, DatePattern: pattern);

    /// <summary>What is wrong with the format (English, for validation), or null.</summary>
    public string? Problem()
        => !Enum.IsDefined(Kind) || !Enum.IsDefined(Separators) || Decimals is < 0 or > MaxDecimals
            ? "The format is not valid."
            : Kind == FormFormatKind.Date && !DatePatterns.Contains(DatePattern, StringComparer.Ordinal)
                ? "The date pattern is not one of those offered."
                : null;

    /// <summary>The value as shown: a number formatted, anything else as it is.</summary>
    public string Display(string? value)
    {
        var text = value ?? string.Empty;
        if (Kind != FormFormatKind.Number || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return text;
        }

        var shown = number.ToString("N" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Separators == NumberSeparators.DotComma ? Swap(shown) : shown;
    }

    /// <summary>
    /// Reads what the user typed into the value kept: a number as a plain number, a date in the pattern. Empty input
    /// is an empty value. False when the input is not a number or a date of this format.
    /// </summary>
    public bool TryNormalize(string input, out string value)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim();
        value = text;
        if (text.Length == 0 || Kind == FormFormatKind.None)
        {
            return true;
        }

        if (Kind == FormFormatKind.Number)
        {
            // Written with this format's separators (thousands in groups of three), or as a plain number "1234.5".
            var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\u00A0", string.Empty, StringComparison.Ordinal);
            var invariant = Separators == NumberSeparators.DotComma ? Swap(compact) : compact;
            if (!GroupedNumber().IsMatch(invariant) && !PlainNumber().IsMatch(invariant = compact))
            {
                return false;
            }

            if (!decimal.TryParse(invariant, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            value = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var pattern = NetPattern(DatePattern);
        var lenient = pattern.Replace("dd", "d", StringComparison.Ordinal).Replace("MM", "M", StringComparison.Ordinal);
        if (!DateTime.TryParseExact(text, [pattern, lenient], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        value = date.ToString(pattern, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The .NET pattern of an Acrobat date pattern (m = month in Acrobat, M in .NET).</summary>
    public static string NetPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Replace('m', 'M');
    }

    [GeneratedRegex(@"^[+-]?([0-9]{1,3}(,[0-9]{3})+|[0-9]+)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupedNumber();

    [GeneratedRegex(@"^[+-]?[0-9]+(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();

    /// <summary>Swaps "," and "." (1,234.56 ⇄ 1.234,56).</summary>
    private static string Swap(string text)
        => string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] switch
                {
                    ',' => '.',
                    '.' => ',',
                    var c => c,
                };
            }
        });
}
