using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPro.Core.Text;

/// <summary>How tone marks and the bar of "đ" count when searching.</summary>
public enum DiacriticsMatching
{
    /// <summary>A query written without marks ignores them ("hoa" finds "hòa", "hóa"); a query with marks matches them.</summary>
    Auto,

    /// <summary>Marks never count.</summary>
    Ignore,

    /// <summary>Marks must match ("hoa" finds only "hoa").</summary>
    Match,
}

/// <param name="MatchCase">Upper and lower case must match.</param>
/// <param name="WholeWord">The match must not be part of a longer word.</param>
/// <param name="Diacritics">How tone marks count.</param>
/// <param name="UseRegex">The query is a regular expression (.NET syntax).</param>
public sealed record TextSearchOptions(
    bool MatchCase = false,
    bool WholeWord = false,
    DiacriticsMatching Diacritics = DiacriticsMatching.Auto,
    bool UseRegex = false);

/// <summary>A match with some text around it, for a list of results (NFC, on one line).</summary>
public sealed record SearchSnippet(string Before, string Match, string After);

/// <summary>
/// Finds a query in the text layer of pages (SPEC "Tiếng Việt"): composed and decomposed letters match alike (NFC = NFD),
/// case and tone marks count as the options say, white space and line ends count as one space so phrases match across
/// lines, and matches come back as character ranges of the page for highlighting and selecting.
/// </summary>
public sealed class TextSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _folded;
    private readonly Regex? _regex;
    private readonly bool _ignoreMarks;
    private readonly TextSearchOptions _options;

    private TextSearch(string folded, Regex? regex, bool ignoreMarks, TextSearchOptions options)
    {
        _folded = folded;
        _regex = regex;
        _ignoreMarks = ignoreMarks;
        _options = options;
    }

    /// <summary>Prepares a search; false with <paramref name="error"/> for an empty query or an invalid expression.</summary>
    public static bool TryCreate(string query, TextSearchOptions options, [NotNullWhen(true)] out TextSearch? search, out string? error)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        search = null;
        error = null;
        if (string.IsNullOrWhiteSpace(query))
        {
            error = "The query is empty.";
            return false;
        }

        var ignoreMarks = options.Diacritics switch
        {
            DiacriticsMatching.Ignore => true,
            DiacriticsMatching.Match => false,
            _ => !HasMarks(query),
        };

        if (options.UseRegex)
        {
            try
            {
                var pattern = ignoreMarks ? RemoveMarks(TextNormalizer.ToNfc(query)) : TextNormalizer.ToNfc(query);
                var regexOptions = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                search = new TextSearch(string.Empty, new Regex(pattern, regexOptions, RegexTimeout), ignoreMarks, options);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        var folded = CollapseSpaces(Fold(query, ignoreMarks, options.MatchCase)).Trim();
        search = new TextSearch(folded, null, ignoreMarks, options);
        return true;
    }

    /// <summary>Every match on the page, in reading order, not overlapping.</summary>
    /// <exception cref="RegexMatchTimeoutException">The expression takes too long on this page.</exception>
    public IReadOnlyList<TextRange> FindAll(PageText page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (text, map) = Prepare(page);
        var matches = new List<TextRange>();
        if (_regex is { } regex)
        {
            for (var match = regex.Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Length > 0 && Accept(text, map, match.Index, match.Index + match.Length))
                {
                    matches.Add(new TextRange(map[match.Index].Start, map[match.Index + match.Length - 1].End));
                }
            }

            return matches;
        }

        var from = 0;
        while (from <= text.Length - _folded.Length)
        {
            var at = text.IndexOf(_folded, from, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            var end = at + _folded.Length;
            if (Accept(text, map, at, end))
            {
                matches.Add(new TextRange(map[at].Start, map[end - 1].End));
                from = end;
            }
            else
            {
                from = at + 1;
            }
        }

        return matches;
    }

    /// <summary>
    /// The match with up to <paramref name="context"/> characters of its page on each side; where the context is cut,
    /// a word cut in two is left out and "…" shows that the text goes on.
    /// </summary>
    public static SearchSnippet Snippet(PageText page, TextRange match, int context = 40)
    {
        ArgumentNullException.ThrowIfNull(page);
        var beforeStart = Math.Max(0, match.Start - context);
        var afterEnd = Math.Min(page.Count, match.End + context);
        var before = TextNormalizer.ToSingleLine(page.GetText(new TextRange(beforeStart, match.Start)));
        var after = TextNormalizer.ToSingleLine(page.GetText(new TextRange(match.End, afterEnd)));
        if (beforeStart > 0)
        {
            var cutsWord = !page.Chars[beforeStart - 1].IsWhiteSpace && !page.Chars[beforeStart].IsWhiteSpace;
            var space = before.IndexOf(' ', StringComparison.Ordinal);
            before = "…" + (cutsWord && space >= 0 ? before[(space + 1)..] : before);
        }

        if (afterEnd < page.Count)
        {
            var cutsWord = !page.Chars[afterEnd - 1].IsWhiteSpace && !page.Chars[afterEnd].IsWhiteSpace;
            var space = after.LastIndexOf(' ');
            after = (cutsWord && space >= 0 ? after[..space] : after) + "…";
        }

        var hit = TextNormalizer.ToSingleLine(page.GetText(match));
        var startsWithSpace = match.Start > 0 && page.Chars[match.Start - 1].IsWhiteSpace;
        var endsWithSpace = match.End < page.Count && page.Chars[match.End].IsWhiteSpace;
        return new SearchSnippet(startsWithSpace && before.Length > 0 ? before + " " : before, hit, endsWithSpace && after.Length > 0 ? " " + after : after);
    }

    /// <summary>
    /// The page text folded like the query, and for each of its characters the page characters it comes from: a letter
    /// and the marks after it fold together; white space and line ends fold to one space.
    /// </summary>
    private (string Text, TextRange[] Map) Prepare(PageText page)
    {
        var builder = new StringBuilder(page.Count);
        var map = new List<TextRange>(page.Count);
        var chars = page.Chars;
        var i = 0;
        while (i < chars.Count)
        {
            if (chars[i].IsWhiteSpace)
            {
                var start = i;
                while (i < chars.Count && chars[i].IsWhiteSpace)
                {
                    i++;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                    map.Add(new TextRange(start, i));
                }

                continue;
            }

            var cluster = i;
            var text = new StringBuilder(chars[i].Text);
            i++;
            while (i < chars.Count && !chars[i].IsWhiteSpace && IsMark(chars[i].Text))
            {
                text.Append(chars[i].Text);
                i++;
            }

            var folded = _regex is null
                ? Fold(text.ToString(), _ignoreMarks, _options.MatchCase)
                : _ignoreMarks ? RemoveMarks(TextNormalizer.ToNfc(text.ToString())) : TextNormalizer.ToNfc(text.ToString());
            foreach (var c in folded)
            {
                builder.Append(c);
                map.Add(new TextRange(cluster, i));
            }
        }

        return (builder.ToString(), [.. map]);
    }

    /// <summary>
    /// A match must cover whole letters (with marks counted, "hoa" is not the start of "hoà") and, for whole words, not
    /// touch letters or digits on either side.
    /// </summary>
    private bool Accept(string text, TextRange[] map, int start, int end)
    {
        if ((start > 0 && map[start - 1] == map[start]) || (end < text.Length && map[end] == map[end - 1]))
        {
            return false;
        }

        if (!_options.WholeWord)
        {
            return true;
        }

        return (start == 0 || !char.IsLetterOrDigit(text[start - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
    }

    /// <summary>
    /// The form both the query and the pages are compared in: decomposed (NFD), marks dropped and "đ" written "d" when
    /// they do not count, lower case unless case counts.
    /// </summary>
    private static string Fold(string text, bool ignoreMarks, bool matchCase)
    {
        var decomposed = TextNormalizer.ToNfc(text).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (ignoreMarks && IsMark(c))
            {
                continue;
            }

            var letter = ignoreMarks ? c switch { 'đ' => 'd', 'Đ' => 'D', _ => c } : c;
            builder.Append(matchCase ? letter : char.ToLowerInvariant(letter));
        }

        return builder.ToString();
    }

    /// <summary>NFC text without tone marks, "đ" as "d" (for regular expressions, which keep their own case handling).</summary>
    private static string RemoveMarks(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (!IsMark(c))
            {
                builder.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool HasMarks(string text)
        => TextNormalizer.ToNfc(text).Normalize(NormalizationForm.FormD).Any(c => IsMark(c) || c is 'đ' or 'Đ');

    private static bool IsMark(string text) => text.Length > 0 && IsMark(text[0]);

    private static bool IsMark(char c)
        => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
