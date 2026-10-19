using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfPro.Core.Organizing;

/// <summary>
/// Identifiers of the files a page plan draws pages from: "s0" is the opened document, "s1"… are inserted files,
/// "r1"… are revisions produced by content operations. Short lowercase ASCII, so they are safe in file names.
/// </summary>
public static partial class SourceIds
{
    /// <summary>The opened document.</summary>
    public const string Original = "s0";

    /// <summary>Reserved for blank pages, which have no source file.</summary>
    public const string Blank = "blank";

    public static bool IsValid(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>The next free id with <paramref name="prefix"/> ('s' for inserted files, 'r' for revisions).</summary>
    /// <param name="prefix">Lowercase ASCII letter.</param>
    /// <param name="existing">Ids already in use.</param>
    /// <param name="first">Number used when no id with this prefix exists yet.</param>
    public static string Next(char prefix, IEnumerable<string> existing, int first = 0)
    {
        if (prefix is < 'a' or > 'z')
        {
            throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "The prefix must be a lowercase ASCII letter.");
        }

        ArgumentNullException.ThrowIfNull(existing);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        var highest = first - 1;
        foreach (var id in existing)
        {
            if (id.Length > 1 && id[0] == prefix
                && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                highest = Math.Max(highest, number);
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{highest + 1}");
    }

    [GeneratedRegex("^[a-z][a-z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
