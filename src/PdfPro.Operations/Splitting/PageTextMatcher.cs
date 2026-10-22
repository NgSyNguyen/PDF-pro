using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;

namespace PdfPro.Operations.Splitting;

/// <summary>
/// Split by text: reads every page's text (PDFium, NFC) and finds the pages where a regular expression matches. A
/// time limit per page keeps a pathological expression from hanging the app.
/// </summary>
public static class PageTextMatcher
{
    /// <summary>Resource key of the progress stage.</summary>
    public const string Stage = "Stage_SplitReadText";

    /// <summary>Longest time one page may take to match.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Longest matched text kept for a file name.</summary>
    private const int MaxMatchLength = 200;

    /// <summary>Compiles the user's pattern (NFC, culture-invariant, ignoring case unless asked).</summary>
    public static bool TryCreate(string? pattern, bool matchCase, [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out string? error)
    {
        regex = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "The pattern is empty.";
            return false;
        }

        var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            regex = new Regex(TextNormalizer.ToNfc(pattern), options, MatchTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The text of every page (NFC); blank pages of the plan give an empty string.</summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(SplitDocument document, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(progress);
        var texts = new string[document.Plan.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, texts.Length, Stage));
            texts[i] = document.SourceOf(i) is { } source
                ? TextNormalizer.ToNfc(await source.Document.GetPageTextAsync(source.PageIndex, cancellationToken).ConfigureAwait(false))
                : string.Empty;
        }

        progress.Report(new OperationProgress(texts.Length, texts.Length, Stage));
        return texts;
    }

    /// <summary>
    /// The first match on every page (white space collapsed), or null where nothing (or only empty text) matches. Pages
    /// that take too long count as not matching and are listed in <paramref name="timedOut"/>.
    /// </summary>
    public static IReadOnlyList<string?> Match(IReadOnlyList<string> texts, Regex regex, out IReadOnlyList<int> timedOut)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(regex);
        var matches = new string?[texts.Count];
        var slow = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            try
            {
                var match = regex.Match(texts[i]);
                matches[i] = match.Success ? Collapse(match.Value) : null;
            }
            catch (RegexMatchTimeoutException)
            {
                slow.Add(i);
            }
        }

        timedOut = slow;
        return matches;
    }

    private static string? Collapse(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            // An empty match ("x*" on any text) does not start a file.
            return null;
        }

        return collapsed.Length <= MaxMatchLength ? collapsed : collapsed[..MaxMatchLength];
    }
}
