using System.Text;
using System.Text.RegularExpressions;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace PdfPro.Operations.Comparing;

/// <summary>A word of a document: its text (NFC), what it is compared by, and where it is.</summary>
internal sealed record ComparedWord(string Text, string Key, int Page, PdfRect Box, int Line);

/// <summary>A line of a document: its page and words, and what it is compared by.</summary>
internal sealed record ComparedLine(int Index, int Page, string Key, IReadOnlyList<ComparedWord> Words);

/// <summary>
/// The text of a document as lines of words in reading order (PdfPig: nearest-neighbour words, Docstrum lines and
/// blocks, unsupervised reading order — as "PDF sang văn bản" reads it), NFC. Running lines — a header or footer whose
/// text, digits aside, comes back near the top or bottom of at least half the pages ("Trang 3/12") — can be left out.
/// </summary>
internal static partial class DocumentWords
{
    /// <summary>Share of the page height at the top and the bottom where headers and footers are looked for.</summary>
    private const double Band = 0.1;

    /// <exception cref="DocumentPasswordException">The document is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static (IReadOnlyList<ComparedLine> Lines, int PageCount) Read(string path, string? password, TextCompareOptions options, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path, new ParsingOptions { Password = password ?? string.Empty, UseLenientParsing = true });
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentPasswordException(DocumentPasswordProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }

        using (document)
        {
            var pages = new List<List<(string Text, PdfRect Box, IReadOnlyList<(string Text, PdfRect Box)> Words, double Height)>>();
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                var lines = new List<(string, PdfRect, IReadOnlyList<(string, PdfRect)>, double)>();
                foreach (var block in ToTextOperation.Blocks(page))
                {
                    foreach (var line in block.TextLines)
                    {
                        var words = line.Words
                            .Select(w => (Text: Clean(w.Text), Box: new PdfRect(w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)))
                            .Where(w => w.Text.Length > 0)
                            .ToList();
                        if (words.Count > 0)
                        {
                            var box = new PdfRect(line.BoundingBox.Left, line.BoundingBox.Bottom, line.BoundingBox.Right, line.BoundingBox.Top);
                            lines.Add((string.Join(' ', words.Select(w => w.Text)), box, words, page.Height));
                        }
                    }
                }

                pages.Add(lines);
            }

            var running = options.IgnoreRunningLines ? RunningLines(pages) : [];
            var result = new List<ComparedLine>();
            for (var p = 0; p < pages.Count; p++)
            {
                foreach (var (text, box, words, height) in pages[p])
                {
                    if (running.Contains(Shape(text)) && InBand(box, height))
                    {
                        continue;
                    }

                    var index = result.Count;
                    var compared = words.Select(w => new ComparedWord(w.Text, Key(w.Text, options), p, w.Box, index)).ToList();
                    result.Add(new ComparedLine(index, p, string.Join(' ', compared.Select(w => w.Key)), compared));
                }
            }

            return (result, document.NumberOfPages);
        }
    }

    /// <summary>The shapes (digits as #) of lines near the top or bottom of at least half the pages (and of two).</summary>
    private static HashSet<string> RunningLines(List<List<(string Text, PdfRect Box, IReadOnlyList<(string, PdfRect)> Words, double Height)>> pages)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            foreach (var shape in page.Where(l => InBand(l.Box, l.Height)).Select(l => Shape(l.Text)).Distinct())
            {
                counts[shape] = counts.GetValueOrDefault(shape) + 1;
            }
        }

        var needed = Math.Max(2, (pages.Count + 1) / 2);
        return [.. counts.Where(c => c.Value >= needed).Select(c => c.Key)];
    }

    private static bool InBand(PdfRect box, double height)
        => height > 0 && (box.Bottom >= height * (1 - Band) || box.Top <= height * Band);

    private static string Shape(string text) => Digits().Replace(text, "#");

    private static string Key(string text, TextCompareOptions options) => options.IgnoreCase ? text.ToLowerInvariant() : text;

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsControl(c) && !char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
