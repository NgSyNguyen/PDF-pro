using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Ocr;

/// <summary>
/// The hidden text of OCR on a page: PDF_Pro's own text layers (content streams and forms marked
/// <see cref="MarkerKey"/>), and text painted with render mode 3 by other OCR programs — in the page's content and in
/// the forms it draws directly (a form is changed in a copy of this page's own, as other pages may draw it too).
/// Text-showing operators in render mode 3 are dropped (' and " keep their move to the next line); the rest of the
/// content, painted text included, is kept byte for byte.
/// </summary>
internal static class InvisibleText
{
    /// <summary>Entry of the content stream and the form of a text layer added by PDF_Pro's OCR.</summary>
    public const string MarkerKey = "/PdfProOcr";

    private const int Invisible = 3;

    /// <summary>Removes the hidden text of a page; true when there was some.</summary>
    /// <exception cref="InvalidDataException">A content stream cannot be decoded.</exception>
    public static bool Remove(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var document = page.Owner;
        var streams = PageStamper.ContentStreams(page);
        var kept = streams.Where(s => AsDictionary(s) is not { } stream || !stream.Elements.ContainsKey(MarkerKey)).ToList();
        var changed = kept.Count != streams.Count;

        var xObjects = AsDictionary(AsDictionary(Resources(page))?.Elements["/XObject"]);
        var content = Join(kept);
        var rewritten = Rewrite(content, 0, name => IsLayer(xObjects, name), out var drawn);
        if (rewritten is not null)
        {
            changed = true;
        }

        var forms = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        foreach (var (name, mode) in drawn)
        {
            if (forms.ContainsKey(name) || AsDictionary(xObjects?.Elements["/" + name]) is not { } form
                || PdfGraph.Name(form, "/Subtype") != "/Form" || form.Stream is null)
            {
                continue;
            }

            var formXObjects = AsDictionary(AsDictionary(form.Elements["/Resources"])?.Elements["/XObject"]);
            if (Rewrite(Bytes(form), mode, n => IsLayer(formXObjects, n), out _) is { } formContent)
            {
                forms[name] = Copy(document, form, formContent);
            }
        }

        if (forms.Count > 0)
        {
            var own = OwnXObjects(page);
            foreach (var (name, form) in forms)
            {
                own.Elements["/" + name] = form.Reference!;
            }

            changed = true;
        }

        if (changed)
        {
            page.Elements["/Contents"] = NewStream(document, rewritten ?? content);
        }

        return changed;
    }

    /// <summary>
    /// The content without the text painted in render mode 3 and without drawing the forms <paramref name="isLayer"/>
    /// names, or null when there is neither; <paramref name="drawn"/> gets the other XObjects drawn and the render mode
    /// at the time.
    /// </summary>
    internal static byte[]? Rewrite(byte[] content, int initialMode, Func<string, bool> isLayer, out List<(string Name, int Mode)> drawn)
    {
        drawn = [];
        var output = new MemoryStream(content.Length);
        var copied = 0;
        var changed = false;
        var mode = initialMode;
        var saved = new Stack<int>();
        var reader = new ContentReader(content);
        while (reader.MoveNext())
        {
            string? replacement = null;
            var drop = false;
            switch (reader.Operator)
            {
                case "q":
                    saved.Push(mode);
                    break;
                case "Q":
                    mode = saved.Count > 0 ? saved.Pop() : initialMode;
                    break;
                case "Tr" when reader.Operands.Count > 0 && reader.Operands[^1].Number is { } value:
                    mode = (int)value;
                    break;
                case "Tj" or "TJ" when mode == Invisible:
                    drop = true;
                    break;
                case "'" when mode == Invisible:
                    replacement = "T*";
                    break;
                case "\"" when mode == Invisible:
                    var word = reader.Operands.Count > 2 ? reader.Operands[0].Number : null;
                    var character = reader.Operands.Count > 2 ? reader.Operands[1].Number : null;
                    replacement = string.Create(CultureInfo.InvariantCulture, $"{word ?? 0} Tw {character ?? 0} Tc T*");
                    break;
                case "Do" when reader.Operands.Count > 0 && reader.Operands[^1].IsName:
                    var name = reader.Operands[^1].Text;
                    if (isLayer(name))
                    {
                        drop = true;
                    }
                    else
                    {
                        drawn.Add((name, mode));
                    }

                    break;
            }

            if (drop || replacement is not null)
            {
                output.Write(content, copied, reader.Start - copied);
                if (replacement is not null)
                {
                    output.Write(Encoding.ASCII.GetBytes(replacement));
                }

                copied = reader.End;
                changed = true;
            }
        }

        if (!changed)
        {
            return null;
        }

        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    private static bool IsLayer(PdfDictionary? xObjects, string name)
        => AsDictionary(xObjects?.Elements["/" + name]) is { } form && form.Elements.ContainsKey(MarkerKey);

    private static PdfItem? Resources(PdfPage page)
    {
        PdfDictionary? node = page;
        while (node is not null)
        {
            if (node.Elements["/Resources"] is { } resources)
            {
                return resources;
            }

            node = AsDictionary(node.Elements["/Parent"]);
        }

        return null;
    }

    /// <summary>A /Resources and /XObject of the page's own, as other pages may share them.</summary>
    private static PdfDictionary OwnXObjects(PdfPage page)
    {
        var document = page.Owner;
        var resources = new PdfDictionary(document);
        if (AsDictionary(Resources(page)) is { } shared)
        {
            foreach (var key in shared.Elements.Keys)
            {
                resources.Elements[key] = shared.Elements[key];
            }
        }

        var xObjects = new PdfDictionary(document);
        if (AsDictionary(resources.Elements["/XObject"]) is { } sharedXObjects)
        {
            foreach (var key in sharedXObjects.Elements.Keys)
            {
                xObjects.Elements[key] = sharedXObjects.Elements[key];
            }
        }

        resources.Elements["/XObject"] = xObjects;
        page.Elements["/Resources"] = resources;
        return xObjects;
    }

    private static byte[] Join(List<PdfItem> streams)
    {
        using var joined = new MemoryStream();
        foreach (var item in streams)
        {
            if (AsDictionary(item) is { Stream: not null } stream)
            {
                joined.Write(Bytes(stream));
                joined.WriteByte((byte)'\n');
            }
        }

        return joined.ToArray();
    }

    private static byte[] Bytes(PdfDictionary stream)
    {
        try
        {
            return StreamData.Bytes(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new InvalidDataException("A content stream cannot be decoded.", ex);
        }
    }

    /// <summary>A copy of a form with new content (deflated); the copy keeps its resources and everything else.</summary>
    private static PdfDictionary Copy(PdfDocument document, PdfDictionary form, byte[] content)
    {
        var copy = new PdfDictionary(document);
        foreach (var key in form.Elements.Keys.Where(k => k is not ("/Length" or "/Filter" or "/DecodeParms")))
        {
            copy.Elements[key] = form.Elements[key];
        }

        copy.CreateStream(Deflate(content));
        copy.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(copy);
        return copy;
    }

    private static PdfReference NewStream(PdfDocument document, byte[] content)
    {
        var stream = new PdfContent(document);
        stream.CreateStream(Deflate(content));
        stream.Elements.SetName("/Filter", "/FlateDecode");
        document.Internals.AddObject(stream);
        return stream.Reference!;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
