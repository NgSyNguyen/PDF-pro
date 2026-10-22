using System.Globalization;
using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Core.Splitting;

/// <summary>
/// File names made from document text (bookmark titles, matched text, names typed by the user) that Windows accepts:
/// NFC, no forbidden characters, no reserved device names, no trailing dot or space, limited length.
/// </summary>
public static class FileNames
{
    /// <summary>Longest generated name in characters, without the extension (keeps full paths well under 260).</summary>
    public const int DefaultMaxLength = 120;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A file name (without extension) Windows accepts, or an empty string when nothing usable is left. Slashes and
    /// bars become "-", the other forbidden characters (: * ? " &lt; &gt;) and control characters are dropped, runs of
    /// white space become one space, and the ends are trimmed of spaces and dots: "Số: 12/QĐ-UBND" → "Số 12-QĐ-UBND".
    /// </summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in TextNormalizer.ToNfc(text))
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var replacement = c switch
            {
                '/' or '\\' or '|' => "-",
                ':' or '*' or '?' or '"' or '<' or '>' => string.Empty,
                _ when char.IsControl(c) => string.Empty,
                _ => null,
            };
            if (replacement == string.Empty)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (replacement is null)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(replacement);
            }
        }

        var name = Truncate(TrimEnds(builder.ToString()), maxLength);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        // "CON", "con.backup", "COM1 "… name devices even with an extension.
        var stem = name.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? name + "_" : name;
    }

    /// <summary>
    /// Makes <paramref name="names"/> distinct (case-insensitive, as Windows compares them) and different from names
    /// already <paramref name="isTaken"/>, by adding " (2)", " (3)"… The order is kept.
    /// </summary>
    /// <param name="isTaken">True when a name is already used (an existing file); null when nothing outside counts.</param>
    public static IReadOnlyList<string> MakeUnique(IReadOnlyList<string> names, Func<string, bool>? isTaken = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(names));
            var candidate = name;
            for (var n = 2; used.Contains(candidate) || (isTaken?.Invoke(candidate) ?? false); n++)
            {
                candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            }

            used.Add(candidate);
            result[i] = candidate;
        }

        return result;
    }

    /// <summary>Cuts to <paramref name="maxLength"/> characters without splitting a letter from its marks.</summary>
    private static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var builder = new StringBuilder(maxLength);
        var elements = StringInfo.GetTextElementEnumerator(name);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length > maxLength)
            {
                break;
            }

            builder.Append(element);
        }

        return TrimEnds(builder.ToString());
    }

    private static string TrimEnds(string name) => name.Trim().TrimEnd('.', ' ').TrimStart(' ');
}
