using PdfPro.Operations.Materializing;

namespace PdfPro.Operations.Security;

/// <summary>What <see cref="ContentSections.Find"/> found in a content stream.</summary>
/// <param name="Spans">Byte ranges to drop, in order, not overlapping.</param>
/// <param name="Unbalanced">Hidden sections left in place because removing them would unbalance q/Q or BT/ET.</param>
internal sealed record ContentSpans(IReadOnlyList<(int Start, int End)> Spans, int Unbalanced);

/// <summary>
/// The bytes of marked-content sections of hidden optional content ("/OC /name BDC … EMC", ISO 32000 8.9.7, 14.6) and
/// of hidden XObjects painted with "/name Do", found by <see cref="ContentReader"/>, so that they can be dropped while
/// every other byte stays as it is.
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
        var depth = 0;
        (int Start, int Depth, int Saves, int Texts)? hidden = null;
        var saves = 0;
        var texts = 0;
        var reader = new ContentReader(content);
        while (reader.MoveNext())
        {
            // The statement runs from its first operand to the end of its operator.
            var (start, at, operands) = (reader.Start, reader.End, reader.Operands);
            switch (reader.Operator)
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
                    if (hidden is null && operands is [{ IsName: true, Text: "OC" }, { IsName: true, Text: var property }] && hiddenProperty(property))
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
                    if (hidden is null && operands is [{ IsName: true, Text: var xObject }] && hiddenXObject(xObject))
                    {
                        spans.Add((start, at));
                    }

                    break;
            }
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
}
