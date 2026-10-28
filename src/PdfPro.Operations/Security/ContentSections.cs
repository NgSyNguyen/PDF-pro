using System.Text;

namespace PdfPro.Operations.Security;

/// <summary>What <see cref="ContentSections.Find"/> found in a content stream.</summary>
/// <param name="Spans">Byte ranges to drop, in order, not overlapping.</param>
/// <param name="Unbalanced">Hidden sections left in place because removing them would unbalance q/Q or BT/ET.</param>
internal sealed record ContentSpans(IReadOnlyList<(int Start, int End)> Spans, int Unbalanced);

/// <summary>
/// A minimal reader of content stream syntax (ISO 32000 7.8.2, 8.9.7, 14.6): enough to find the bytes of marked-content
/// sections of hidden optional content ("/OC /name BDC … EMC") and of hidden XObjects painted with "/name Do", so that
/// they can be dropped while every other byte stays as it is. Inline images (BI … ID … EI) are skipped whole.
/// </summary>
internal static class ContentSections
{
    /// <summary>Upper bound for the nesting of marked content (defends against hostile streams).</summary>
    private const int MaxDepth = 256;

    /// <summary>
    /// The sections to drop: marked content tagged /OC whose property name <paramref name="hiddenProperty"/> says is
    /// hidden, and "Do" of an XObject <paramref name="hiddenXObject"/> says is hidden. A section is kept when it does not
    /// close the q and BT it opens (or closes ones it did not open): removing it would change what follows.
    /// </summary>
    public static ContentSpans Find(ReadOnlySpan<byte> content, Func<string, bool> hiddenProperty, Func<string, bool> hiddenXObject)
    {
        var spans = new List<(int Start, int End)>();
        var unbalanced = 0;
        var operands = new List<(bool IsName, string Text)>();
        var statement = -1; // where the operands of the current operator start
        var depth = 0;
        (int Start, int Depth, int Saves, int Texts)? hidden = null;
        var saves = 0;
        var texts = 0;
        var at = 0;
        while (at < content.Length)
        {
            var c = content[at];
            if (IsWhite(c))
            {
                at++;
                continue;
            }

            if (c == '%')
            {
                while (at < content.Length && content[at] is not ((byte)'\n' or (byte)'\r'))
                {
                    at++;
                }

                continue;
            }

            if (statement < 0)
            {
                statement = at;
            }

            switch (c)
            {
                case (byte)'/':
                    var nameEnd = TokenEnd(content, at + 1);
                    operands.Add((true, Name(content[(at + 1)..nameEnd])));
                    at = nameEnd;
                    continue;
                case (byte)'(':
                    at = LiteralEnd(content, at);
                    operands.Add((false, string.Empty));
                    continue;
                case (byte)'<' when at + 1 < content.Length && content[at + 1] == '<':
                case (byte)'>' when at + 1 < content.Length && content[at + 1] == '>':
                    at += 2;
                    operands.Add((false, string.Empty));
                    continue;
                case (byte)'<':
                    var close = content[at..].IndexOf((byte)'>');
                    at = close < 0 ? content.Length : at + close + 1;
                    operands.Add((false, string.Empty));
                    continue;
                case (byte)'[' or (byte)']' or (byte)'{' or (byte)'}':
                    at++;
                    operands.Add((false, string.Empty));
                    continue;
            }

            var end = TokenEnd(content, at);
            if (end == at)
            {
                at++; // a stray delimiter (")" or ">"): skip it
                continue;
            }

            var token = Encoding.Latin1.GetString(content[at..end]);
            if (char.IsAsciiDigit(token[0]) || token[0] is '+' or '-' or '.' || token is "true" or "false" or "null")
            {
                operands.Add((false, token));
                at = end;
                continue;
            }

            // An operator: the statement runs from its first operand to its end.
            var start = statement;
            at = token == "BI" ? InlineImageEnd(content, end) : end;
            switch (token)
            {
                case "q":
                    saves++;
                    break;
                case "Q":
                    saves--;
                    break;
                case "BT":
                    texts++;
                    break;
                case "ET":
                    texts--;
                    break;
                case "BMC":
                    depth = Math.Min(depth + 1, MaxDepth);
                    break;
                case "BDC":
                    if (hidden is null && operands is [(true, "OC"), (true, var property)] && hiddenProperty(property))
                    {
                        hidden = (start, depth, saves, texts);
                    }

                    depth = Math.Min(depth + 1, MaxDepth);
                    break;
                case "EMC":
                    depth = Math.Max(depth - 1, 0);
                    if (hidden is { } open && depth == open.Depth)
                    {
                        Close(open, at);
                        hidden = null;
                    }

                    break;
                case "Do":
                    if (hidden is null && operands is [(true, var xObject)] && hiddenXObject(xObject))
                    {
                        spans.Add((start, at));
                    }

                    break;
            }

            operands.Clear();
            statement = -1;
        }

        if (hidden is { } last)
        {
            // Never closed: the hidden content runs to the end of the stream.
            Close(last, content.Length);
        }

        return new ContentSpans(spans, unbalanced);

        void Close((int Start, int Depth, int Saves, int Texts) open, int end)
        {
            if (open.Saves == saves && open.Texts == texts)
            {
                spans.Add((open.Start, end));
            }
            else
            {
                unbalanced++;
            }
        }
    }

    /// <summary>The content without the spans (each replaced by one space, so that tokens stay apart).</summary>
    public static byte[] Remove(ReadOnlySpan<byte> content, IReadOnlyList<(int Start, int End)> spans)
    {
        using var output = new MemoryStream(content.Length);
        var at = 0;
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (start < at)
            {
                continue;
            }

            output.Write(content[at..start]);
            output.WriteByte((byte)' ');
            at = end;
        }

        output.Write(content[at..]);
        return output.ToArray();
    }

    private static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static int TokenEnd(ReadOnlySpan<byte> content, int from)
    {
        var at = from;
        while (at < content.Length && !IsWhite(content[at]) && !IsDelimiter(content[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>A name with its #xx escapes decoded.</summary>
    private static string Name(ReadOnlySpan<byte> raw)
    {
        var bytes = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length && byte.TryParse(Encoding.ASCII.GetString(raw.Slice(i + 1, 2)), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                bytes.Add(value);
                i += 2;
            }
            else
            {
                bytes.Add(raw[i]);
            }
        }

        return Encoding.Latin1.GetString([.. bytes]);
    }

    /// <summary>Just after the literal string opened at <paramref name="open"/> (balanced parentheses, escapes).</summary>
    private static int LiteralEnd(ReadOnlySpan<byte> content, int open)
    {
        var depth = 0;
        for (var i = open; i < content.Length; i++)
        {
            switch (content[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return content.Length;
    }

    /// <summary>
    /// Just after the "EI" closing an inline image whose "BI" ends at <paramref name="from"/>: its dictionary up to
    /// "ID", one white-space byte, the data, then "EI" between white space (or at the end).
    /// </summary>
    private static int InlineImageEnd(ReadOnlySpan<byte> content, int from)
    {
        var at = from;
        while (at < content.Length)
        {
            while (at < content.Length && IsWhite(content[at]))
            {
                at++;
            }

            if (at + 1 < content.Length && content[at] == 'I' && content[at + 1] == 'D' && (at + 2 == content.Length || IsWhite(content[at + 2])))
            {
                at += 3;
                break;
            }

            at = content[at] == '(' ? LiteralEnd(content, at) : Math.Max(TokenEnd(content, at), at + 1);
        }

        for (var i = at; i + 1 < content.Length; i++)
        {
            if (content[i] == 'E' && content[i + 1] == 'I' && i > at && IsWhite(content[i - 1]) && (i + 2 == content.Length || IsWhite(content[i + 2])))
            {
                return i + 2;
            }
        }

        return content.Length;
    }
}
