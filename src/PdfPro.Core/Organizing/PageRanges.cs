using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.Text;

namespace PdfPro.Core.Organizing;

public enum PageRangeErrorKind
{
    /// <summary>The text names no page at all.</summary>
    Empty,

    /// <summary>A part of the text is not a page number, a range or a keyword.</summary>
    InvalidSyntax,

    /// <summary>A page number is 0 or beyond the last page.</summary>
    OutOfRange,
}

/// <summary>Why a page range could not be read; <see cref="Token"/> is the offending part of the text.</summary>
public sealed record PageRangeError(PageRangeErrorKind Kind, string Token);

/// <summary>
/// Page ranges as people type them, with 1-based page numbers: "1-5, 8, 10-" (10 to the end), "-3" (1 to 3),
/// "odd"/"lẻ", "even"/"chẵn", "all"/"tất cả". Parts are separated by commas, semicolons or spaces; a range may be
/// written backwards ("5-3"). Used by the page organizer, printing, splitting and the command line.
/// </summary>
public static partial class PageRanges
{
    /// <summary>Reads <paramref name="text"/>; on success the 0-based indices are sorted and distinct.</summary>
    public static bool TryParse(
        string? text,
        int pageCount,
        [NotNullWhen(true)] out IReadOnlyList<int>? indices,
        [NotNullWhen(false)] out PageRangeError? error)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var selected = new SortedSet<int>();
        error = null;
        foreach (var rawPart in TextNormalizer.ToNfc(text ?? string.Empty).Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // A part is either one range (spaces allowed around the dash) or several space-separated items.
            var items = RangePattern().IsMatch(rawPart) || Keyword(rawPart) is not null
                ? [rawPart]
                : rawPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in items)
            {
                error = AddItem(item, pageCount, selected);
                if (error is not null)
                {
                    indices = null;
                    return false;
                }
            }
        }

        if (selected.Count == 0)
        {
            indices = null;
            error = new PageRangeError(PageRangeErrorKind.Empty, text?.Trim() ?? string.Empty);
            return false;
        }

        indices = [.. selected];
        return true;
    }

    /// <exception cref="FormatException">The text is empty, malformed or names a page that does not exist.</exception>
    public static IReadOnlyList<int> Parse(string text, int pageCount)
        => TryParse(text, pageCount, out var indices, out var error)
            ? indices
            : throw new FormatException($"Invalid page range ({error.Kind}): '{error.Token}'.");

    /// <summary>Writes 0-based indices as a compact 1-based range, e.g. [0, 1, 2, 6] → "1-3, 7".</summary>
    public static string Format(IEnumerable<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        var sorted = indices.Distinct().Order().ToArray();
        if (sorted.Length > 0 && sorted[0] < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(indices), sorted[0], "Page indices must not be negative.");
        }

        var text = new StringBuilder();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append((sorted[i] + 1).ToString(CultureInfo.InvariantCulture));
            if (end > i)
            {
                text.Append('-').Append((sorted[end] + 1).ToString(CultureInfo.InvariantCulture));
            }

            i = end + 1;
        }

        return text.ToString();
    }

    private static PageRangeError? AddItem(string item, int pageCount, SortedSet<int> selected)
    {
        if (Keyword(item) is { } keyword)
        {
            for (var page = keyword == Parity.Even ? 2 : 1; page <= pageCount; page += keyword == Parity.All ? 1 : 2)
            {
                selected.Add(page - 1);
            }

            return null;
        }

        var range = RangePattern().Match(item);
        int first;
        int last;
        if (range.Success)
        {
            if (!range.Groups["from"].Success && !range.Groups["to"].Success)
            {
                return new PageRangeError(PageRangeErrorKind.InvalidSyntax, item);
            }

            if (!TryReadPage(range.Groups["from"], 1, out first) || !TryReadPage(range.Groups["to"], pageCount, out last))
            {
                return new PageRangeError(PageRangeErrorKind.OutOfRange, item);
            }
        }
        else if (NumberPattern().IsMatch(item))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out first))
            {
                return new PageRangeError(PageRangeErrorKind.OutOfRange, item);
            }

            last = first;
        }
        else
        {
            return new PageRangeError(PageRangeErrorKind.InvalidSyntax, item);
        }

        if (first > last)
        {
            (first, last) = (last, first);
        }

        if (first < 1 || last > pageCount)
        {
            return new PageRangeError(PageRangeErrorKind.OutOfRange, item);
        }

        for (var page = first; page <= last; page++)
        {
            selected.Add(page - 1);
        }

        return null;
    }

    /// <summary>Reads an optional number group; a missing number means <paramref name="whenMissing"/>.</summary>
    private static bool TryReadPage(Group group, int whenMissing, out int page)
    {
        if (!group.Success)
        {
            page = whenMissing;
            return true;
        }

        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out page);
    }

    private static Parity? Keyword(string item) => item.ToLowerInvariant() switch
    {
        "odd" or "lẻ" => Parity.Odd,
        "even" or "chẵn" => Parity.Even,
        "all" or "tất cả" => Parity.All,
        _ => null,
    };

    [GeneratedRegex(@"^(?<from>[0-9]+)?\s*[-–]\s*(?<to>[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    private enum Parity
    {
        Odd,
        Even,
        All,
    }
}
