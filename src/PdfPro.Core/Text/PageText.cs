using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Text;

/// <summary>One character of a page's text layer, as the page draws it (or as the text extractor adds it).</summary>
/// <param name="Text">The character: one UTF-16 unit or a surrogate pair; may be a combining mark of the previous letter.</param>
/// <param name="Box">Where it is drawn, in the page's PDF user space (font ascent to descent); empty for generated characters.</param>
/// <param name="IsGenerated">Not drawn by the page: a space or line end the extractor inserted between words and lines.</param>
/// <param name="IsHyphen">A hyphen breaking a word at the end of a line.</param>
public sealed record TextChar(string Text, PdfRect Box, bool IsGenerated = false, bool IsHyphen = false)
{
    public bool IsLineBreak => Text is "\r" or "\n";

    public bool IsWhiteSpace => string.IsNullOrWhiteSpace(Text);

    /// <summary>Drawn on the page with a box that can be pointed at.</summary>
    public bool IsDrawn => !IsGenerated && !Box.IsEmpty;
}

/// <summary>Characters <see cref="Start"/> (included) to <see cref="End"/> (excluded) of a page's text layer.</summary>
public readonly record struct TextRange(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);

    public bool IsEmpty => End <= Start;

    /// <summary>The characters from <paramref name="first"/> to <paramref name="last"/>, both included, in either order.</summary>
    public static TextRange Between(int first, int last) => first <= last ? new(first, last + 1) : new(last, first + 1);

    public bool Contains(int index) => index >= Start && index < End;
}

/// <summary>
/// The text layer of a page: its characters in content order with their boxes, split into lines at the line ends the
/// extractor inserts. Pointing (character at a point, word, line), the rectangles a selection covers, and the text a
/// selection copies (NFC, lines of a paragraph joined, words broken by a hyphen at a line end mended).
/// </summary>
public sealed class PageText
{
    private const string ParagraphBreak = "\r\n";
    private const string SentenceEnds = ".:!?…;";

    private readonly TextRange[] _lines;
    private readonly int[] _lineOfChar;

    public PageText(IReadOnlyList<TextChar> chars)
    {
        ArgumentNullException.ThrowIfNull(chars);
        Chars = chars;
        _lineOfChar = new int[chars.Count];
        var lines = new List<TextRange>();
        var start = 0;
        for (var i = 0; i <= chars.Count; i++)
        {
            if (i == chars.Count || chars[i].IsLineBreak)
            {
                if (i > start)
                {
                    lines.Add(new TextRange(start, i));
                }

                start = i + 1;
            }
        }

        _lines = [.. lines];
        Array.Fill(_lineOfChar, -1);
        for (var line = 0; line < _lines.Length; line++)
        {
            for (var i = _lines[line].Start; i < _lines[line].End; i++)
            {
                _lineOfChar[i] = line;
            }
        }
    }

    public static PageText Empty { get; } = new([]);

    public IReadOnlyList<TextChar> Chars { get; }

    public int Count => Chars.Count;

    /// <summary>The lines, without their line ends, in content order.</summary>
    public IReadOnlyList<TextRange> Lines => _lines;

    /// <summary>The page draws text (a scanned page without a text layer draws none).</summary>
    public bool HasText => Chars.Any(c => c.IsDrawn && !c.IsWhiteSpace);

    /// <summary>The whole layer as the extractor gives it (not normalized).</summary>
    public string Text => GetText(new TextRange(0, Count));

    /// <summary>
    /// The drawn character under <paramref name="point"/>, or the nearest one no farther than
    /// <paramref name="tolerance"/> points; null when there is none.
    /// </summary>
    public int? CharAt(PdfPoint point, double tolerance = 0)
    {
        int? nearest = null;
        var best = double.MaxValue;
        for (var i = 0; i < Count; i++)
        {
            var c = Chars[i];
            if (!c.IsDrawn)
            {
                continue;
            }

            if (c.Box.Contains(point))
            {
                return i;
            }

            var distance = Distance(c.Box, point);
            if (distance <= tolerance && distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The character a selection dragged to <paramref name="point"/> reaches: on the line nearest the point vertically,
    /// the last character starting left of it (or the line's first). Null for a page without drawn characters.
    /// </summary>
    public int? CaretAt(PdfPoint point)
    {
        int? bestLine = null;
        var bestDistance = double.MaxValue;
        for (var line = 0; line < _lines.Length; line++)
        {
            var box = LineBox(_lines[line]);
            if (box.IsEmpty)
            {
                continue;
            }

            var distance = point.Y > box.Top ? point.Y - box.Top : point.Y < box.Bottom ? box.Bottom - point.Y : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLine = line;
            }
        }

        if (bestLine is not { } found)
        {
            return null;
        }

        var range = _lines[found];
        int? caret = null;
        for (var i = range.Start; i < range.End; i++)
        {
            if (Chars[i].IsDrawn && (caret is null || Chars[i].Box.Left <= point.X))
            {
                caret = i;
            }
        }

        return caret;
    }

    /// <summary>The word around character <paramref name="index"/> (letters, digits and marks), or the character alone.</summary>
    public TextRange WordAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        if (!IsWordChar(Chars[index]))
        {
            return new TextRange(index, index + 1);
        }

        var start = index;
        while (start > 0 && IsWordChar(Chars[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < Count && IsWordChar(Chars[end]))
        {
            end++;
        }

        return new TextRange(start, end);
    }

    /// <summary>The line holding character <paramref name="index"/> (a line end gives the line before it).</summary>
    public TextRange LineAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        var line = LineIndexOf(index);
        return line >= 0 ? _lines[line] : new TextRange(index, index + 1);
    }

    /// <summary>The rectangles <paramref name="range"/> covers on the page: one per line, around its drawn characters.</summary>
    public IReadOnlyList<PdfRect> Rects(TextRange range)
    {
        var rects = new List<PdfRect>();
        foreach (var line in _lines)
        {
            var part = new TextRange(Math.Max(line.Start, range.Start), Math.Min(line.End, range.End));
            if (!part.IsEmpty && LineBox(part) is { IsEmpty: false } box)
            {
                rects.Add(box);
            }
        }

        return rects;
    }

    /// <summary>The characters of <paramref name="range"/> as they are (not normalized, line ends included).</summary>
    public string GetText(TextRange range)
    {
        var builder = new StringBuilder();
        for (var i = Math.Max(0, range.Start); i < Math.Min(Count, range.End); i++)
        {
            builder.Append(Chars[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// What copying <paramref name="range"/> puts on the clipboard: NFC; lines of one paragraph joined by a space, a word
    /// broken by a hyphen at a line end mended; a paragraph ends where the next line starts after a wider gap, is
    /// indented, or follows a short line ending a sentence.
    /// </summary>
    public string CopyText(TextRange range)
    {
        var parts = new List<(string Text, PdfRect Box, bool EndsWithHyphen)>();
        foreach (var line in _lines)
        {
            var part = new TextRange(Math.Max(line.Start, range.Start), Math.Min(line.End, range.End));
            if (part.IsEmpty)
            {
                continue;
            }

            var text = GetText(part);
            var lastDrawn = Enumerable.Range(part.Start, part.Length).LastOrDefault(i => !Chars[i].IsWhiteSpace, -1);
            var hyphen = lastDrawn >= 0 && (Chars[lastDrawn].IsHyphen || Chars[lastDrawn].Text is "-" or "­") && part.End == line.End;
            parts.Add((text.TrimEnd(), LineBox(line), hyphen));
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var left = parts.Where(p => !p.Box.IsEmpty).Select(p => p.Box.Left).DefaultIfEmpty(0).Min();
        var right = parts.Where(p => !p.Box.IsEmpty).Select(p => p.Box.Right).DefaultIfEmpty(0).Max();
        var builder = new StringBuilder(parts[0].Text);
        for (var i = 1; i < parts.Count; i++)
        {
            var previous = parts[i - 1];
            var next = parts[i];
            if (previous.EndsWithHyphen && next.Text.Length > 0 && char.IsLetter(next.Text.TrimStart()[0]))
            {
                builder.Length -= 1; // the hyphen
                builder.Append(next.Text.TrimStart());
                continue;
            }

            builder.Append(EndsParagraph(previous.Text, previous.Box, next.Box, left, right) ? ParagraphBreak : " ");
            builder.Append(next.Text.TrimStart());
        }

        return TextNormalizer.ToNfc(builder.ToString());
    }

    /// <summary>The line holding character <paramref name="index"/>, or -1 for a line end.</summary>
    public int LineIndexOf(int index) => index >= 0 && index < Count ? _lineOfChar[index] : -1;

    private PdfRect LineBox(TextRange range)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        for (var i = range.Start; i < range.End; i++)
        {
            var c = Chars[i];
            if (!c.IsDrawn)
            {
                continue;
            }

            left = Math.Min(left, c.Box.Left);
            bottom = Math.Min(bottom, c.Box.Bottom);
            right = Math.Max(right, c.Box.Right);
            top = Math.Max(top, c.Box.Top);
        }

        return left <= right ? new PdfRect(left, bottom, right, top) : default;
    }

    private static bool EndsParagraph(string previousText, PdfRect previous, PdfRect next, double left, double right)
    {
        if (previous.IsEmpty || next.IsEmpty)
        {
            return true;
        }

        var height = Math.Max(previous.Height, 1);
        var gap = previous.Bottom - next.Top;
        if (gap > height * 0.6 || gap < -height)
        {
            return true; // a wider gap than the line spacing, or the next line is not below (another column)
        }

        if (next.Left > left + (height * 1.2))
        {
            return true; // indented first line
        }

        var endsSentence = previousText.Length > 0 && SentenceEnds.Contains(previousText[^1], StringComparison.Ordinal);
        return endsSentence && previous.Right < right - (height * 3);
    }

    private static bool IsWordChar(TextChar c)
    {
        if (c.IsGenerated || c.IsWhiteSpace || c.Text.Length == 0)
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(c.Text, 0);
        return char.IsLetterOrDigit(c.Text, 0)
            || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
    }

    private static double Distance(PdfRect box, PdfPoint point)
    {
        var dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
        var dy = point.Y < box.Bottom ? box.Bottom - point.Y : point.Y > box.Top ? point.Y - box.Top : 0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
