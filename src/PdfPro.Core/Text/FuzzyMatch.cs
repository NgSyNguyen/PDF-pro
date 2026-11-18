using System.Globalization;
using System.Text;

namespace PdfPro.Core.Text;

/// <summary>
/// Matching typed words against command names the way a command palette does (SPEC Phase 12): without tone marks
/// ("xoa trang trang" finds "Xóa trang trắng", "đ" as "d"), without case, by word starts ("xtt"), by every word typed
/// in any order ("trang xoa"), or by letters in order. A higher score is a better match; null is none.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>The form names are compared in: no tone marks, "đ" as "d", lower case, single spaces.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in TextNormalizer.ToNfc(text).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(char.ToLowerInvariant(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c }));
        }

        return builder.ToString();
    }

    /// <summary>How well <paramref name="query"/> matches <paramref name="candidate"/>; null when it does not.</summary>
    public static int? Score(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        var q = Fold(query);
        var c = Fold(candidate);
        return q.Length == 0 ? 0 : ScoreFolded(q, c);
    }

    /// <summary><see cref="Score"/> for text already folded with <see cref="Fold"/>.</summary>
    public static int? ScoreFolded(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);
        if (query.Length == 0)
        {
            return 0;
        }

        if (candidate == query)
        {
            return 1000;
        }

        if (candidate.StartsWith(query, StringComparison.Ordinal))
        {
            return 900 - Math.Min(99, candidate.Length - query.Length);
        }

        var atWord = WordStartIndex(candidate, query);
        if (atWord >= 0)
        {
            return 800 - Math.Min(99, atWord);
        }

        var inside = candidate.IndexOf(query, StringComparison.Ordinal);
        if (inside >= 0)
        {
            return 600 - Math.Min(99, inside);
        }

        var initials = string.Concat(candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
        if (!query.Contains(' ', StringComparison.Ordinal) && initials.StartsWith(query, StringComparison.Ordinal))
        {
            return 500 - Math.Min(99, initials.Length - query.Length);
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1 && terms.All(t => candidate.Contains(t, StringComparison.Ordinal)))
        {
            // Words found at word starts, near the beginning, score better.
            var position = terms.Sum(t => WordStartIndex(candidate, t) is var start and >= 0 ? start : candidate.IndexOf(t, StringComparison.Ordinal) + 20) / terms.Length;
            return 400 - Math.Min(99, position);
        }

        return Subsequence(query.Replace(" ", string.Empty, StringComparison.Ordinal), candidate);
    }

    /// <summary>Where <paramref name="query"/> starts a word of <paramref name="candidate"/>, or -1.</summary>
    private static int WordStartIndex(string candidate, string query)
    {
        for (var at = candidate.IndexOf(query, StringComparison.Ordinal); at >= 0; at = candidate.IndexOf(query, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !char.IsLetterOrDigit(candidate[at - 1]))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>The letters typed found in order: better when they follow each other or start words, worse with gaps.</summary>
    private static int? Subsequence(string query, string candidate)
    {
        var score = 100;
        var position = 0;
        var previous = -2;
        foreach (var letter in query)
        {
            var found = candidate.IndexOf(letter, position);
            if (found < 0)
            {
                return null;
            }

            score += found == previous + 1 ? 6 : found == 0 || !char.IsLetterOrDigit(candidate[found - 1]) ? 4 : -Math.Min(5, found - position);
            previous = found;
            position = found + 1;
        }

        return Math.Clamp(score, 1, 399);
    }
}
