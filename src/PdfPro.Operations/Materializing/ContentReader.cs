using System.Globalization;
using System.Text;

namespace PdfPro.Operations.Materializing;

/// <summary>An operand of a content stream operator: a name (without "/"), a number or keyword token, or another value (empty text).</summary>
internal readonly record struct ContentOperand(bool IsName, string Text)
{
    /// <summary>A string, array, dictionary or bracket: nothing more is kept of it.</summary>
    public static readonly ContentOperand Other = new(false, string.Empty);

    /// <summary>The number the operand is, or null.</summary>
    public double? Number => !IsName && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}

/// <summary>
/// A minimal reader of content stream syntax (ISO 32000 7.8.2): the statements — operands, then their operator — one
/// at a time, with the bytes each one spans. Strings, arrays and dictionaries are skipped as one operand each (their
/// contents are not needed), and inline images (BI … ID … EI) are one "BI" statement spanning the whole image.
/// </summary>
internal ref struct ContentReader(ReadOnlySpan<byte> content)
{
    private readonly ReadOnlySpan<byte> _content = content;
    private int _at;

    /// <summary>Where the current statement starts (its first operand, or its operator).</summary>
    public int Start { get; private set; }

    /// <summary>Just after the current statement's operator (after "EI" for an inline image).</summary>
    public int End { get; private set; }

    public string Operator { get; private set; } = string.Empty;

    /// <summary>The operands of the current statement (the list is reused by <see cref="MoveNext"/>).</summary>
    public List<ContentOperand> Operands { get; } = [];

    /// <summary>Reads the next statement; false at the end of the content (operands without an operator are dropped).</summary>
    public bool MoveNext()
    {
        var content = _content;
        Operands.Clear();
        var statement = -1;
        while (_at < content.Length)
        {
            var c = content[_at];
            if (IsWhite(c))
            {
                _at++;
                continue;
            }

            if (c == '%')
            {
                while (_at < content.Length && content[_at] is not ((byte)'\n' or (byte)'\r'))
                {
                    _at++;
                }

                continue;
            }

            if (statement < 0)
            {
                statement = _at;
            }

            switch (c)
            {
                case (byte)'/':
                    var nameEnd = TokenEnd(content, _at + 1);
                    Operands.Add(new ContentOperand(true, Name(content[(_at + 1)..nameEnd])));
                    _at = nameEnd;
                    continue;
                case (byte)'(':
                    _at = LiteralEnd(content, _at);
                    Operands.Add(ContentOperand.Other);
                    continue;
                case (byte)'<' when _at + 1 < content.Length && content[_at + 1] == '<':
                case (byte)'>' when _at + 1 < content.Length && content[_at + 1] == '>':
                    _at += 2;
                    Operands.Add(ContentOperand.Other);
                    continue;
                case (byte)'<':
                    var close = content[_at..].IndexOf((byte)'>');
                    _at = close < 0 ? content.Length : _at + close + 1;
                    Operands.Add(ContentOperand.Other);
                    continue;
                case (byte)'[' or (byte)']' or (byte)'{' or (byte)'}':
                    _at++;
                    Operands.Add(ContentOperand.Other);
                    continue;
            }

            var end = TokenEnd(content, _at);
            if (end == _at)
            {
                _at++; // a stray delimiter (")" or ">"): skip it
                continue;
            }

            var token = Encoding.Latin1.GetString(content[_at..end]);
            if (char.IsAsciiDigit(token[0]) || token[0] is '+' or '-' or '.' || token is "true" or "false" or "null")
            {
                Operands.Add(new ContentOperand(false, token));
                _at = end;
                continue;
            }

            Start = statement;
            Operator = token;
            _at = token == "BI" ? InlineImageEnd(content, end) : end;
            End = _at;
            return true;
        }

        return false;
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
            if (raw[i] == '#' && i + 2 < raw.Length && byte.TryParse(Encoding.ASCII.GetString(raw.Slice(i + 1, 2)), NumberStyles.HexNumber, null, out var value))
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

            at = at < content.Length && content[at] == '(' ? LiteralEnd(content, at) : Math.Max(TokenEnd(content, at), at + 1);
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
