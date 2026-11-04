using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// Saving with PDFsharp without it rewriting what a document says about itself. On every save PDFsharp names itself in
/// the document information (/Creator, /Producer; dates too for a new document) when they are missing, writes its name
/// in a comment after the trailer, and points the catalog's /Metadata at an XMP packet of its own (its name, the time
/// of saving) in place of the document's. That would put back what a document never had, or what "Làm sạch" removed,
/// and lose the document's own XMP. After saving, the written file is patched in place with bytes of the same length
/// (the cross-reference table stays right): the added entries and the comment become spaces, /Metadata points at the
/// document's own XMP again (or at nothing) and PDFsharp's packet is blanked out.
/// </summary>
internal static partial class PdfWriting
{
    private static readonly string[] Names = ["/Creator", "/Producer", "/CreationDate", "/ModDate"];

    /// <summary>Writes the document to <paramref name="output"/> (left open), saying about itself only what it said before.</summary>
    public static void Save(PdfDocument document, Stream output)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var added = Names.Where(n => !document.Info.Elements.ContainsKey(n)).ToList();
        var catalog = document.Internals.Catalog;
        var ownMetadata = catalog.Elements["/Metadata"] is PdfReference { Value: PdfDictionary metadata } ? metadata : null;
        if (ownMetadata is not null)
        {
            // Kept reachable while PDFsharp replaces /Metadata, so that it is written; the entry is blanked afterwards.
            catalog.Elements[KeepKey] = ownMetadata.Reference!;
        }

        try
        {
            if (output.CanSeek && output.CanRead)
            {
                var start = output.Position;
                document.Save(output, closeStream: false);

                // PDFsharp may leave the position elsewhere than at the end of what it wrote.
                var end = output.Length;
                Patch(output, start, end, added, ownMetadata);
                output.Position = end;
                return;
            }

            using var buffer = new MemoryStream();
            document.Save(buffer, closeStream: false);
            Patch(buffer, 0, buffer.Length, added, ownMetadata);
            buffer.Position = 0;
            buffer.CopyTo(output);
        }
        finally
        {
            catalog.Elements.Remove(KeepKey);
        }
    }

    /// <summary>A catalog entry of our own holding the document's XMP while PDFsharp saves.</summary>
    private const string KeepKey = "/PdfProOwnMetadata";

    private static void Patch(Stream file, long start, long end, IReadOnlyCollection<string> added, PdfDictionary? ownMetadata)
    {
        if (added.Count > 0)
        {
            Blank(file, start, end, added);
        }

        RestoreMetadata(file, start, end, ownMetadata);
        BlankComment(file, start, end);
    }

    /// <summary>The "% Created with PDFsharp…" comment PDFsharp writes after the trailer, overwritten with spaces.</summary>
    private static void BlankComment(Stream file, long start, long end)
    {
        var tailLength = (int)Math.Min(4096, end - start);
        var tail = Read(file, end - tailLength, tailLength);
        var startXref = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        var comment = startXref < 0 ? -1 : tail.LastIndexOf("% Created with PDFsharp", startXref, StringComparison.Ordinal);
        if (comment < 0)
        {
            return;
        }

        var lineEnd = tail.IndexOfAny(['\r', '\n'], comment);
        if (lineEnd > comment && lineEnd <= startXref)
        {
            Overwrite(file, end - tailLength + comment, new string(' ', lineEnd - comment));
        }
    }

    /// <summary>
    /// Blanks the given entries of the information dictionary of the file written between <paramref name="start"/> and
    /// <paramref name="end"/>; false (nothing changed) when the file is not laid out as PDFsharp writes it.
    /// </summary>
    internal static bool Blank(Stream file, long start, long end, IReadOnlyCollection<string> names)
    {
        if (Trailer(file, start, end) is not { Info: { } info } trailer
            || ReadObject(file, start, end, trailer.Table, info) is not { } found)
        {
            return false;
        }

        var (at, text, open, close) = found;
        var blanked = false;
        foreach (var name in names)
        {
            if (Entry(text, open, close, name) is { } span)
            {
                Overwrite(file, start + at + span.Start, new string(' ', span.Length));
                blanked = true;
            }
        }

        return blanked;
    }

    /// <summary>
    /// Points the catalog's /Metadata back at the document's own XMP (when it was written) or removes it, and blanks
    /// the packet PDFsharp wrote in its place. False when nothing was changed.
    /// </summary>
    internal static bool RestoreMetadata(Stream file, long start, long end, PdfDictionary? ownMetadata)
    {
        if (Trailer(file, start, end) is not { } trailer
            || ReadObject(file, start, end, trailer.Table, trailer.Root) is not { } catalog
            || Entry(catalog.Text, catalog.Open, catalog.Close, "/Metadata") is not { } span
            || Reference().Match(catalog.Text, span.Start, span.Length) is not { Success: true } written)
        {
            return false;
        }

        var generated = int.Parse(written.Groups[1].Value, CultureInfo.InvariantCulture);
        var replacement = string.Empty;
        if (ownMetadata?.Reference is { } own && own.ObjectNumber != generated
            && ReadObject(file, start, end, trailer.Table, own.ObjectNumber) is not null)
        {
            replacement = string.Create(CultureInfo.InvariantCulture, $"/Metadata {own.ObjectNumber} {own.GenerationNumber} R");
        }
        else if (ownMetadata?.Reference is { } same && same.ObjectNumber == generated)
        {
            return false; // PDFsharp kept the document's own packet
        }

        if (replacement.Length > span.Length)
        {
            return false;
        }

        Overwrite(file, start + catalog.At + span.Start, replacement.PadRight(span.Length));
        if (Entry(catalog.Text, catalog.Open, catalog.Close, KeepKey) is { } kept)
        {
            Overwrite(file, start + catalog.At + kept.Start, new string(' ', kept.Length));
        }

        BlankStream(file, start, end, trailer.Table, generated);
        return true;
    }

    /// <summary>The data of a stream object overwritten with spaces (it is no longer referenced).</summary>
    private static void BlankStream(Stream file, long start, long end, long table, int number)
    {
        if (Offset(file, start, start + table, end, number) is not { } offset)
        {
            return;
        }

        var length = (int)Math.Min(1 << 20, end - (start + offset));
        var text = Read(file, start + offset, length);
        var data = text.IndexOf("stream", StringComparison.Ordinal);
        var stop = text.IndexOf("endstream", StringComparison.Ordinal);
        if (data < 0 || stop < data)
        {
            return;
        }

        data += "stream".Length;
        data += text[data] == '\r' && text[data + 1] == '\n' ? 2 : 1;
        if (stop > data)
        {
            Overwrite(file, start + offset + data, new string(' ', stop - data));
        }
    }

    private sealed record TrailerInfo(long Table, int Root, int? Info);

    /// <summary>The trailer at the end of the file: where the cross-reference table is, the catalog, the information.</summary>
    private static TrailerInfo? Trailer(Stream file, long start, long end)
    {
        var tailLength = (int)Math.Min(4096, end - start);
        var tail = Read(file, end - tailLength, tailLength);
        var trailer = tail.LastIndexOf("trailer", StringComparison.Ordinal);
        var xrefAt = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (trailer < 0 || xrefAt < trailer
            || StartXref().Match(tail, xrefAt) is not { Success: true } startXref
            || !long.TryParse(startXref.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var table)
            || RootReference().Match(tail, trailer, xrefAt - trailer) is not { Success: true } root)
        {
            return null;
        }

        var info = InfoReference().Match(tail, trailer, xrefAt - trailer);
        return new TrailerInfo(
            table,
            int.Parse(root.Groups[1].Value, CultureInfo.InvariantCulture),
            info.Success ? int.Parse(info.Groups[1].Value, CultureInfo.InvariantCulture) : null);
    }

    /// <summary>A dictionary object: where it starts (from the start of the file), its text, where its dictionary opens and closes.</summary>
    private static (long At, string Text, int Open, int Close)? ReadObject(Stream file, long start, long end, long table, int number)
    {
        if (Offset(file, start, start + table, end, number) is not { } offset)
        {
            return null;
        }

        var length = (int)Math.Min(64 * 1024, end - (start + offset));
        var text = Read(file, start + offset, length);
        var header = ObjectHeader().Match(text);
        if (!header.Success || header.Index != 0 || int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture) != number)
        {
            return null;
        }

        var open = text.IndexOf("<<", header.Length, StringComparison.Ordinal);
        var close = open < 0 ? -1 : DictionaryEnd(text, open);
        return close < 0 ? null : (offset, text, open, close);
    }

    /// <summary>The offset (from the start of the file) of object <paramref name="number"/> in a classic cross-reference table.</summary>
    private static long? Offset(Stream file, long start, long tableAt, long end, int number)
    {
        file.Position = tableAt;
        using var reader = new StreamReader(file, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        if (reader.ReadLine()?.Trim() != "xref")
        {
            return null;
        }

        while (reader.ReadLine() is { } line && !line.StartsWith("trailer", StringComparison.Ordinal))
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var first) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var count))
            {
                return null;
            }

            if (number < first || number >= first + count)
            {
                for (var i = 0; i < count; i++)
                {
                    reader.ReadLine();
                }

                continue;
            }

            for (var i = first; i < number; i++)
            {
                reader.ReadLine();
            }

            var entry = reader.ReadLine()?.Trim().Split(' ');
            return entry is [var at, _, "n"] && long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && start + offset < end ? offset : null;
        }

        return null;
    }

    /// <summary>Where the dictionary opened at <paramref name="open"/> ends (its "&gt;&gt;"), or -1.</summary>
    private static int DictionaryEnd(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length - 1; i++)
        {
            switch (text[i])
            {
                case '(':
                    i = StringEnd(text, i);
                    if (i < 0)
                    {
                        return -1;
                    }

                    break;
                case '<' when text[i + 1] == '<':
                    depth++;
                    i++;
                    break;
                case '>' when text[i + 1] == '>':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    i++;
                    break;
            }
        }

        return -1;
    }

    /// <summary>The span of "/Name value" at the top level of the dictionary (value: a string or a reference), or null.</summary>
    private static (int Start, int Length)? Entry(string text, int open, int close, string name)
    {
        var at = open + 2;
        var depth = 0;
        while (at < close)
        {
            var c = text[at];
            if (c == '(')
            {
                at = StringEnd(text, at) + 1;
                continue;
            }

            if (c == '<' && text[at + 1] == '<')
            {
                depth++;
                at += 2;
                continue;
            }

            if (c == '>' && text[at + 1] == '>')
            {
                depth--;
                at += 2;
                continue;
            }

            if (depth == 0 && string.CompareOrdinal(text, at, name, 0, name.Length) == 0 && !IsNameChar(text[at + name.Length]))
            {
                var value = at + name.Length;
                while (value < close && char.IsWhiteSpace(text[value]))
                {
                    value++;
                }

                int valueEnd;
                if (text[value] == '(')
                {
                    valueEnd = StringEnd(text, value);
                }
                else if (text[value] == '<' && text[value + 1] != '<')
                {
                    valueEnd = text.IndexOf('>', value);
                }
                else if (Reference().Match(text, value) is { Success: true } reference && reference.Index == value)
                {
                    valueEnd = value + reference.Length - 1;
                }
                else
                {
                    valueEnd = -1;
                }

                return valueEnd < 0 ? null : (at, valueEnd + 1 - at);
            }

            at++;
        }

        return null;
    }

    /// <summary>The closing parenthesis of the literal string opened at <paramref name="open"/>, or -1.</summary>
    private static int StringEnd(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static bool IsNameChar(char c) => !char.IsWhiteSpace(c) && c is not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%');

    private static string Read(Stream file, long at, int length)
    {
        var bytes = new byte[length];
        file.Position = at;
        file.ReadExactly(bytes);
        return Encoding.Latin1.GetString(bytes);
    }

    private static void Overwrite(Stream file, long at, string text)
    {
        file.Position = at;
        file.Write(Encoding.Latin1.GetBytes(text));
    }

    [GeneratedRegex(@"/Info\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex InfoReference();

    [GeneratedRegex(@"/Root\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex RootReference();

    [GeneratedRegex(@"(\d+)\s+(\d+)\s+R")]
    private static partial Regex Reference();

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"^(\d+)\s+(\d+)\s+obj\b")]
    private static partial Regex ObjectHeader();
}
