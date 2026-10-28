using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Core.Redaction;

/// <summary>What kind of personal data a pattern finds ("Tìm &amp; đánh dấu").</summary>
public enum RedactionPattern
{
    /// <summary>Số CCCD: 12 digits.</summary>
    NationalId,

    /// <summary>Vietnamese mobile (0/+84 + 3x, 5x, 7x, 8x, 9x and 8 digits) or landline (0/+84 + 2 and 9 digits) numbers.</summary>
    Phone,

    Email,

    /// <summary>Số tài khoản: 9–19 digits (spaces, dots or dashes between them allowed) that are not a CCCD or a phone number.</summary>
    BankAccount,

    /// <summary>Dates such as a birth date: d/m/yyyy with "/", "." or "-".</summary>
    Date,

    /// <summary>The user's own regular expression.</summary>
    Custom,
}

/// <summary>
/// A match of a pattern on a page: its characters and the rectangles that cover them, one per line (PDF space of the
/// page). <see cref="Text"/> is personal data: it is shown to the user for review only — never logged, and
/// <see cref="ToString"/> leaves it out.
/// </summary>
public sealed record RedactionHit(int PageIndex, RedactionPattern Pattern, string Text, TextRange Range, IReadOnlyList<PdfRect> Rects)
{
    public override string ToString() => $"RedactionHit {{ page {PageIndex + 1}, {Pattern}, {Range.Length} characters }}";
}

/// <summary>
/// Finds personal data in the text layer of a page by pattern (SPEC P8 B), on the text as it reads (white space and line
/// ends count as one space, letters NFC). Matches do not overlap: where two patterns find the same characters, the one
/// listed first in <see cref="RedactionPattern"/> keeps them.
/// </summary>
public static partial class RedactionFinder
{
    private static readonly TextSearchOptions Exact = new(MatchCase: true, Diacritics: DiacriticsMatching.Match, UseRegex: true);

    /// <summary>The regular expression of each built-in pattern (.NET syntax, on the page text).</summary>
    public static IReadOnlyDictionary<RedactionPattern, string> Expressions { get; } = new Dictionary<RedactionPattern, string>
    {
        [RedactionPattern.NationalId] = @"(?<![\d])\d{12}(?![\d])",
        [RedactionPattern.Phone] = @"(?<![\d+])(?:\+84|84|0)[ .\-]?(?:[35789](?:[ .\-]?\d){8}|2(?:[ .\-]?\d){9})(?![\d])",
        [RedactionPattern.Email] = @"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}(?![A-Za-z0-9])",
        [RedactionPattern.BankAccount] = @"(?<![\d+])\d(?:[ .\-]?\d){8,18}(?![\d])",
        [RedactionPattern.Date] = @"(?<![\d])(?:0?[1-9]|[12]\d|3[01])[/.\-](?:0?[1-9]|1[0-2])[/.\-](?:19|20)\d{2}(?![\d])",
    };

    /// <summary>Prepares the user's expression; false with a message (English, from .NET) when it is not valid.</summary>
    public static bool TryCreateCustom(string expression, [NotNullWhen(true)] out TextSearch? search, out string? error)
        => TextSearch.TryCreate(expression, Exact, out search, out error);

    /// <summary>The matches of <paramref name="patterns"/> on a page, in reading order.</summary>
    /// <param name="page">The page's text layer.</param>
    /// <param name="pageIndex">The page (0-based), carried into the hits.</param>
    /// <param name="patterns">The patterns to look for (<see cref="RedactionPattern.Custom"/> uses <paramref name="custom"/>).</param>
    /// <param name="custom">The user's expression, from <see cref="TryCreateCustom"/>.</param>
    /// <exception cref="RegexMatchTimeoutException">The user's expression takes too long on this page.</exception>
    public static IReadOnlyList<RedactionHit> Find(PageText page, int pageIndex, IReadOnlyCollection<RedactionPattern> patterns, TextSearch? custom = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(patterns);
        var hits = new List<RedactionHit>();
        if (!page.HasText)
        {
            return hits;
        }

        foreach (var pattern in Enum.GetValues<RedactionPattern>().Where(patterns.Contains))
        {
            var search = pattern == RedactionPattern.Custom ? custom : Search(pattern);
            if (search is null)
            {
                continue;
            }

            foreach (var range in search.FindAll(page))
            {
                var text = TextNormalizer.ToNfc(page.GetText(range)).Trim();
                if (text.Length == 0 || hits.Any(h => Overlap(h.Range, range)) || (pattern == RedactionPattern.BankAccount && IsOtherNumber(text)))
                {
                    continue;
                }

                hits.Add(new RedactionHit(pageIndex, pattern, text, range, page.Rects(range)));
            }
        }

        hits.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));
        return hits;
    }

    private static TextSearch Search(RedactionPattern pattern)
        => TextSearch.TryCreate(Expressions[pattern], Exact, out var search, out var error)
            ? search
            : throw new InvalidOperationException($"Built-in pattern {pattern} is not valid: {error}");

    private static bool Overlap(TextRange a, TextRange b) => a.Start < b.End && b.Start < a.End;

    /// <summary>A number the other patterns describe better: twelve digits in a row (CCCD) or a phone number.</summary>
    private static bool IsOtherNumber(string text)
        => NationalIdOnly().IsMatch(text) || PhoneOnly().IsMatch(text);

    [GeneratedRegex(@"^\d{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex NationalIdOnly();

    [GeneratedRegex(@"^(?:\+84|84|0)[ .\-]?(?:[35789](?:[ .\-]?\d){8}|2(?:[ .\-]?\d){9})$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneOnly();
}
