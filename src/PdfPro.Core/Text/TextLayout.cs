using System.Globalization;
using System.Text;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Text;

/// <summary>
/// The measurements of one font face that text layout needs, in design units: read from the font file itself, so the
/// screen and the PDF writer measure exactly alike.
/// </summary>
public interface IFontMetrics
{
    int UnitsPerEm { get; }

    /// <summary>Height above the baseline (positive).</summary>
    int Ascender { get; }

    /// <summary>Depth below the baseline (negative or 0).</summary>
    int Descender { get; }

    int LineGap { get; }

    /// <summary>Advance width of the glyph drawn for <paramref name="codePoint"/> (the missing-glyph width when absent).</summary>
    int Advance(int codePoint);
}

/// <summary>A piece of a line placed at <see cref="X"/> points from the left of the layout box.</summary>
public sealed record TextSegment(string Text, double X, double Width);

/// <summary>A laid-out line: its text, its pieces (one, or one per word for justified lines) and its baseline from the top.</summary>
public sealed record TextLine(string Text, IReadOnlyList<TextSegment> Segments, double Baseline, double Width);

/// <summary>The lines of a text, its total height and its line height, in points.</summary>
public sealed record TextLayoutResult(IReadOnlyList<TextLine> Lines, double Height, double LineHeight, double Ascent);

/// <summary>
/// Breaks text into lines for a box width, aligns them and places their baselines — the one layout used by both the
/// on-screen editor and the PDF writer, so text breaks at the same words in both. Text is normalized to NFC; lines
/// break at spaces, and a word wider than the box breaks between characters (never inside a combining sequence).
/// Widths are plain advance sums (no kerning).
/// </summary>
public static class TextLayout
{
    /// <summary>Lays out <paramref name="text"/>; <paramref name="maxWidth"/> ≤ 0 or infinite means no wrapping.</summary>
    public static TextLayoutResult Layout(string text, IFontMetrics metrics, double fontSize, double maxWidth, TextAlignment alignment = TextAlignment.Left, double lineSpacing = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fontSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineSpacing);

        var unit = fontSize / metrics.UnitsPerEm;
        var ascent = metrics.Ascender * unit;
        var descent = -metrics.Descender * unit;
        var lineHeight = (metrics.Ascender - metrics.Descender + metrics.LineGap) * unit * lineSpacing;
        var wrap = double.IsFinite(maxWidth) && maxWidth > 0;
        var measure = new Measurer(metrics, unit);

        var lines = new List<TextLine>();
        foreach (var paragraph in Paragraphs(text))
        {
            var broken = wrap ? Break(paragraph, measure, maxWidth) : [paragraph.TrimEnd(' ')];
            for (var i = 0; i < broken.Count; i++)
            {
                var isLast = i == broken.Count - 1;
                lines.Add(Place(broken[i], measure, wrap ? maxWidth : 0, alignment, isLast, ascent + (lines.Count * lineHeight)));
            }
        }

        var height = ascent + descent + ((lines.Count - 1) * lineHeight);
        return new TextLayoutResult(lines, height, lineHeight, ascent);
    }

    /// <summary>Width in points of <paramref name="text"/> on one line.</summary>
    public static double Measure(string text, IFontMetrics metrics, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metrics);
        return new Measurer(metrics, fontSize / metrics.UnitsPerEm).Width(TextNormalizer.ToNfc(text));
    }

    private static IEnumerable<string> Paragraphs(string text)
        => TextNormalizer.ToNfc(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\t', ' ').Split('\n');

    private static List<string> Break(string paragraph, Measurer measure, double maxWidth)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in Words(paragraph))
        {
            if (line.Length > 0 && measure.Width((line + word).TrimEnd(' ')) > maxWidth)
            {
                lines.Add(line.ToString().TrimEnd(' '));
                line.Clear();
            }

            if (line.Length == 0 && measure.Width(word.TrimEnd(' ')) > maxWidth)
            {
                // A word longer than the line: break it between characters.
                lines.AddRange(BreakWord(word, measure, maxWidth, out var rest));
                line.Append(rest);
                continue;
            }

            line.Append(word);
        }

        lines.Add(line.ToString().TrimEnd(' '));
        return lines;
    }

    /// <summary>Words with their trailing spaces; leading spaces stay with the first word.</summary>
    private static List<string> Words(string paragraph)
    {
        var words = new List<string>();
        var (start, i) = (0, 0);
        while (i < paragraph.Length && paragraph[i] == ' ')
        {
            i++;
        }

        while (i < paragraph.Length)
        {
            while (i < paragraph.Length && paragraph[i] != ' ')
            {
                i++;
            }

            while (i < paragraph.Length && paragraph[i] == ' ')
            {
                i++;
            }

            words.Add(paragraph[start..i]);
            start = i;
        }

        return words;
    }

    private static List<string> BreakWord(string word, Measurer measure, double maxWidth, out string rest)
    {
        var pieces = new List<string>();
        var piece = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(word);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (piece.Length > 0 && element != " " && measure.Width(piece + element) > maxWidth)
            {
                pieces.Add(piece.ToString());
                piece.Clear();
            }

            piece.Append(element);
        }

        rest = piece.ToString();
        return pieces;
    }

    private static TextLine Place(string line, Measurer measure, double maxWidth, TextAlignment alignment, bool isLastOfParagraph, double baseline)
    {
        var width = measure.Width(line);
        if (alignment == TextAlignment.Justify && !isLastOfParagraph && maxWidth > width && line.Trim().Contains(' ', StringComparison.Ordinal))
        {
            var leading = line.Length - line.TrimStart(' ').Length;
            var words = line.TrimStart(' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wordsWidth = words.Sum(measure.Width);
            var x = measure.Width(line[..leading]);
            var gap = (maxWidth - x - wordsWidth) / (words.Length - 1);
            var segments = new List<TextSegment>(words.Length);
            foreach (var word in words)
            {
                var w = measure.Width(word);
                segments.Add(new TextSegment(word, x, w));
                x += w + gap;
            }

            return new TextLine(line, segments, baseline, maxWidth);
        }

        var left = alignment switch
        {
            _ when maxWidth <= 0 => 0,
            TextAlignment.Center => (maxWidth - width) / 2,
            TextAlignment.Right => maxWidth - width,
            _ => 0,
        };
        return new TextLine(line, line.Length == 0 ? [] : [new TextSegment(line, left, width)], baseline, width);
    }

    private sealed class Measurer(IFontMetrics metrics, double unit)
    {
        public double Width(string text)
        {
            var units = 0L;
            foreach (var rune in text.EnumerateRunes())
            {
                units += metrics.Advance(rune.Value);
            }

            return units * unit;
        }
    }
}
