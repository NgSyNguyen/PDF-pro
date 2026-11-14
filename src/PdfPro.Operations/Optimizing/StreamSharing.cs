using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>
/// Streams that are byte for byte the same — the same picture or embedded font program in each of several merged
/// files, a colour profile repeated on every page — become one object that everything refers to; the copies are no
/// longer referenced and PDFsharp does not write them. Two streams are the same when their data and their dictionaries
/// (apart from /Length) are equal, references compared by the object they point to.
/// </summary>
internal static class StreamSharing
{
    /// <summary>Streams never shared: cross-reference and object streams (PDFsharp writes its own).</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal) { "/XRef", "/ObjStm" };

    /// <summary>Rounds of sharing (sharing pictures can make the forms that draw them equal in turn).</summary>
    private const int MaxRounds = 3;

    /// <summary>Shares the duplicate streams; how many copies are no longer used.</summary>
    public static int Share(PdfDocument document, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replaced = Round(document);
            total += replaced;
            if (replaced == 0)
            {
                break;
            }
        }

        return total;
    }

    private static int Round(PdfDocument document)
    {
        var originals = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        var copies = new Dictionary<PdfObject, PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfDictionary { Stream.Value: { } data, Reference: not null } stream || Name(stream, "/Type") is { } type && Excluded.Contains(type))
            {
                continue;
            }

            var key = Key(stream, data);
            if (originals.TryGetValue(key, out var original))
            {
                copies[stream] = original;
            }
            else
            {
                originals[key] = stream;
            }
        }

        if (copies.Count == 0)
        {
            return 0;
        }

        Redirect(document, copies);
        return copies.Count;
    }

    /// <summary>Every reference to a copy in the document now points at its original.</summary>
    private static void Redirect(PdfDocument document, Dictionary<PdfObject, PdfDictionary> copies)
    {
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem>(document.Internals.GetAllObjects());
        pending.Push(document.Internals.Catalog);
        while (pending.TryPop(out var item))
        {
            switch (item)
            {
                case PdfDictionary dictionary when seen.Add(dictionary):
                    foreach (var key in dictionary.Elements.Keys.ToArray())
                    {
                        var value = dictionary.Elements[key];
                        if (Target(value, copies) is { } original)
                        {
                            dictionary.Elements[key] = original.Reference!;
                        }
                        else if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case PdfArray array when seen.Add(array):
                    for (var i = 0; i < array.Elements.Count; i++)
                    {
                        var value = array.Elements[i];
                        if (Target(value, copies) is { } original)
                        {
                            array.Elements[i] = original.Reference!;
                        }
                        else if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
            }
        }
    }

    private static PdfDictionary? Target(PdfItem? value, Dictionary<PdfObject, PdfDictionary> copies)
        => value is PdfReference { Value: { } target } && copies.TryGetValue(target, out var original) ? original : null;

    /// <summary>A digest of the stream's data and of its dictionary without /Length.</summary>
    private static string Key(PdfDictionary stream, byte[] data)
    {
        var text = new StringBuilder();
        Write(stream, text, 0, skipLength: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
        hash.AppendData([0]);
        hash.AppendData(data);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>A canonical text of a value: keys sorted, references as "n g R", direct objects written out.</summary>
    private static void Write(PdfItem? item, StringBuilder text, int depth, bool skipLength = false)
    {
        if (depth > 32)
        {
            text.Append('…');
            return;
        }

        switch (item)
        {
            case null:
                text.Append("null");
                break;
            case PdfReference reference:
                text.Append(reference.ObjectNumber).Append(' ').Append(reference.GenerationNumber).Append(" R");
                break;
            case PdfDictionary dictionary:
                text.Append("<<");
                foreach (var key in dictionary.Elements.Keys.Order(StringComparer.Ordinal))
                {
                    if (skipLength && key == "/Length")
                    {
                        continue;
                    }

                    text.Append(key).Append(' ');
                    Write(dictionary.Elements[key], text, depth + 1);
                    text.Append(' ');
                }

                text.Append(">>");
                break;
            case PdfArray array:
                text.Append('[');
                foreach (var element in array.Elements)
                {
                    Write(element, text, depth + 1);
                    text.Append(' ');
                }

                text.Append(']');
                break;
            case PdfString value:
                text.Append('(').Append(Convert.ToHexString(Encoding.UTF8.GetBytes(value.Value))).Append(')');
                break;
            default:
                text.Append(item.GetType().Name).Append(':').Append(item);
                break;
        }
    }
}
