using System.Text.RegularExpressions;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Security;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace PdfPro.Operations.Structure;

/// <summary>How headings are recognised for bookmarks.</summary>
public enum HeadingRule
{
    /// <summary>By their text being larger than the body text (the largest size is level 1).</summary>
    FontSize,

    /// <summary>By patterns, one per level ("^Chương [IVXLC]+" for level 1, "^Điều \d+" for level 2).</summary>
    Patterns,
}

/// <param name="Rule">Font size or patterns.</param>
/// <param name="Patterns">With <see cref="HeadingRule.Patterns"/>: one regular expression per level, level 1 first.</param>
/// <param name="MinRatio">With <see cref="HeadingRule.FontSize"/>: a heading is at least this many times the body size.</param>
/// <param name="MaxLevels">With <see cref="HeadingRule.FontSize"/>: the heading sizes kept, largest first.</param>
public sealed record BookmarkGenerationOptions(HeadingRule Rule = HeadingRule.FontSize, IReadOnlyList<string>? Patterns = null, double MinRatio = 1.15, int MaxLevels = 3);

/// <summary>
/// Bookmarks made from a document's headings, in reading order (PdfPig lines and blocks, as "PDF sang văn bản" reads
/// them): lines in larger type than the body (sizes ranked into levels; a heading cut over two lines is one) or lines
/// matching a pattern per level. Each bookmark opens its page at the heading.
/// </summary>
public static class BookmarkGenerator
{
    /// <summary>A heading is at most this long (longer lines are text in a large size).</summary>
    public const int MaxTitle = 160;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <exception cref="DocumentPasswordException">The document is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    /// <exception cref="ArgumentException">A pattern is not a valid regular expression.</exception>
    public static Task<IReadOnlyList<BookmarkNode>> GenerateAsync(string path, string? password, BookmarkGenerationOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        var patterns = (options.Patterns ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PatternTimeout))
            .ToList();
        if (options.Rule == HeadingRule.Patterns && patterns.Count == 0)
        {
            throw new ArgumentException("Give at least one pattern.", nameof(options));
        }

        return Task.Run<IReadOnlyList<BookmarkNode>>(
            () =>
            {
                using var document = Open(path, password);
                var lines = new List<Line>();
                for (var p = 1; p <= document.NumberOfPages; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = document.GetPage(p);
                    foreach (var block in ToTextOperation.Blocks(page))
                    {
                        lines.AddRange(block.TextLines.Select((l, i) => new Line(p - 1, TextNormalizer.ToNfc(l.Text.Trim()), l.BoundingBox.Top, Size(l.Words.SelectMany(w => w.Letters)), block, i)));
                    }
                }

                var headings = options.Rule == HeadingRule.Patterns ? ByPatterns(lines, patterns) : BySize(lines, options);
                return Tree(headings);
            },
            cancellationToken);
    }

    private static List<(int Level, Line Line)> ByPatterns(List<Line> lines, List<Regex> patterns)
    {
        var headings = new List<(int, Line)>();
        foreach (var line in lines.Where(l => l.Text.Length is > 0 and <= MaxTitle))
        {
            for (var level = 0; level < patterns.Count; level++)
            {
                try
                {
                    if (patterns[level].IsMatch(line.Text))
                    {
                        headings.Add((level, line));
                        break;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
            }
        }

        return headings;
    }

    private static List<(int Level, Line Line)> BySize(List<Line> lines, BookmarkGenerationOptions options)
    {
        var body = lines.Where(l => l.Size > 0).GroupBy(l => Math.Round(l.Size * 2) / 2).MaxBy(g => g.Sum(l => l.Text.Length))?.Key ?? 0;
        if (body <= 0)
        {
            return [];
        }

        var large = lines.Where(l => l.Size >= body * options.MinRatio && l.Text.Length is > 0 and <= MaxTitle && l.Text.Any(char.IsLetter)).ToList();
        var sizes = large.Select(l => Math.Round(l.Size * 2) / 2).Distinct().OrderDescending().Take(Math.Max(1, options.MaxLevels)).ToList();
        var headings = new List<(int Level, Line Line)>();
        foreach (var line in large)
        {
            var level = sizes.IndexOf(Math.Round(line.Size * 2) / 2);
            if (level < 0)
            {
                continue;
            }

            // A heading cut over two lines of one block is one heading.
            if (headings.Count > 0 && headings[^1] is var (lastLevel, last) && lastLevel == level && ReferenceEquals(last.Block, line.Block) && last.Index + 1 == line.Index)
            {
                headings[^1] = (level, last with { Text = $"{last.Text} {line.Text}", Index = line.Index });
                continue;
            }

            headings.Add((level, line));
        }

        return headings;
    }

    /// <summary>Headings in order as a tree: each under the last heading of a higher level.</summary>
    private static List<BookmarkNode> Tree(List<(int Level, Line Line)> headings)
    {
        var roots = new List<Builder>();
        var stack = new List<Builder>();
        foreach (var (level, line) in headings)
        {
            var title = line.Text.Length > MaxTitle ? line.Text[..MaxTitle] : line.Text;
            var node = new Builder(level, new BookmarkNode(title, line.Page, Math.Round(line.Top + 4, 1)));
            while (stack.Count > 0 && stack[^1].Level >= level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            (stack.Count == 0 ? roots : stack[^1].Children).Add(node);
            stack.Add(node);
        }

        return [.. roots.Select(r => r.Build())];
    }

    private static double Size(IEnumerable<Letter> letters)
    {
        var sizes = letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => l.PointSize).Order().ToList();
        return sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
    }

    internal static PdfDocument Open(string path, string? password)
    {
        var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }

        try
        {
            return PdfDocument.Open(path, options);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentPasswordException(DocumentPasswordProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    private sealed record Line(int Page, string Text, double Top, double Size, object Block, int Index);

    private sealed class Builder(int level, BookmarkNode node)
    {
        public int Level => level;

        public List<Builder> Children { get; } = [];

        public BookmarkNode Build() => Children.Count == 0 ? node : node with { Children = [.. Children.Select(c => c.Build())] };
    }
}

/// <summary>
/// Web addresses and e-mail addresses written in a document's text, as links to make (structure.links): "https://…",
/// "www.…" (made https) and "ten@mien.vn" (made mailto:), each over the words it is written in; addresses that already
/// have a link over them are left out.
/// </summary>
public static partial class LinkDetector
{
    /// <exception cref="DocumentPasswordException">The document is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static Task<IReadOnlyList<LinkSpec>> FindAsync(string path, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run<IReadOnlyList<LinkSpec>>(
            () =>
            {
                List<LinkInfo> existing;
                using (var structure = Attachments.Open(path, password))
                {
                    existing = DocumentStructureReader.Links(structure);
                }

                using var document = BookmarkGenerator.Open(path, password);
                var found = new List<LinkSpec>();
                for (var p = 1; p <= document.NumberOfPages; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var words = document.GetPage(p).GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                    foreach (var word in words)
                    {
                        var text = TextNormalizer.ToNfc(word.Text);
                        if (Address(text) is not { } match)
                        {
                            continue;
                        }

                        var box = Box(word, match.Index, match.Length, text.Length);
                        if (existing.Any(l => l.Page == p - 1 && Overlap(l.Rect, box) > 0.5))
                        {
                            continue;
                        }

                        found.Add(new LinkSpec(p - 1, box, new LinkTarget(Url(match.Value))));
                    }
                }

                return found;
            },
            cancellationToken);
    }

    /// <summary>The address in a word, or null ("(xem https://a.vn)." gives "https://a.vn").</summary>
    internal static Match? Address(string word)
    {
        var match = Web().Match(word);
        if (!match.Success)
        {
            match = Email().Match(word);
        }

        return match.Success ? match : null;
    }

    /// <summary>The address as a link: "www." made https, an e-mail made mailto.</summary>
    internal static string Url(string address)
    {
        var trimmed = address.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?', '"', '\'');
        if (trimmed.Contains('@', StringComparison.Ordinal) && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            return "mailto:" + trimmed;
        }

        return trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + trimmed : trimmed;
    }

    /// <summary>The part of a word's box its address covers (letters are spread evenly).</summary>
    private static PdfRect Box(Word word, int start, int length, int total)
    {
        var box = word.BoundingBox;
        var letters = word.Letters;
        if (letters.Count == total && total > 0)
        {
            var first = letters[start].BoundingBox;
            var last = letters[Math.Min(total - 1, start + length - 1)].BoundingBox;
            return new PdfRect(Math.Min(first.Left, last.Left), box.Bottom, Math.Max(first.Right, last.Right), box.Top);
        }

        return new PdfRect(box.Left, box.Bottom, box.Right, box.Top);
    }

    private static double Overlap(PdfRect a, PdfRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return width > 0 && height > 0 ? width * height / Math.Max(0.01, b.Width * b.Height) : 0;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""()\[\]]+[^\s<>""()\[\].,;:!?']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Web();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();
}
