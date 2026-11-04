using PdfSharp.Pdf;
using PdfSharp.Pdf.Filters;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>The filters of a stream (7.4), and its data with the general-purpose ones undone.</summary>
internal static class StreamData
{
    /// <summary>Filters undone here: everything but the image codecs (DCT, JPX, CCITT, JBIG2) and Crypt.</summary>
    private static readonly Dictionary<string, string> General = new(StringComparer.Ordinal)
    {
        ["/FlateDecode"] = "/FlateDecode",
        ["/Fl"] = "/FlateDecode",
        ["/LZWDecode"] = "/LZWDecode",
        ["/LZW"] = "/LZWDecode",
        ["/ASCIIHexDecode"] = "/ASCIIHexDecode",
        ["/AHx"] = "/ASCIIHexDecode",
        ["/ASCII85Decode"] = "/ASCII85Decode",
        ["/A85"] = "/ASCII85Decode",
        ["/RunLengthDecode"] = "/RunLengthDecode",
        ["/RL"] = "/RunLengthDecode",
    };

    /// <summary>The image codecs by full name (abbreviations of inline images included).</summary>
    private static readonly Dictionary<string, string> Codecs = new(StringComparer.Ordinal)
    {
        ["/DCTDecode"] = "/DCTDecode",
        ["/DCT"] = "/DCTDecode",
        ["/CCITTFaxDecode"] = "/CCITTFaxDecode",
        ["/CCF"] = "/CCITTFaxDecode",
        ["/JBIG2Decode"] = "/JBIG2Decode",
        ["/JPXDecode"] = "/JPXDecode",
    };

    /// <summary>The filters (full names) with their parameters, in the order they are undone; null when unreadable.</summary>
    public static IReadOnlyList<(string Name, PdfDictionary? Parameters)>? Filters(PdfDictionary stream)
    {
        var filter = Resolve(stream.Elements["/Filter"]);
        var parameters = Resolve(stream.Elements["/DecodeParms"]);
        switch (filter)
        {
            case null:
                return [];
            case PdfName name:
                return FullName(name.Value) is { } full ? [(full, parameters as PdfDictionary)] : null;
            case PdfArray array:
                var result = new List<(string, PdfDictionary?)>(array.Elements.Count);
                for (var i = 0; i < array.Elements.Count; i++)
                {
                    if (Resolve(array.Elements[i]) is not PdfName item || FullName(item.Value) is not { } itemName)
                    {
                        return null;
                    }

                    result.Add((itemName, parameters is PdfArray list && i < list.Elements.Count ? AsDictionary(list.Elements[i]) : null));
                }

                return result;
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="name"/> (a full filter name) is undone by <see cref="Decode"/>.</summary>
    public static bool IsGeneral(string name) => General.ContainsKey(name);

    /// <summary>
    /// The stream's data with its leading general-purpose filters undone; <paramref name="remaining"/> are the filters
    /// left (an image codec and what follows it). Null when the data cannot be decoded.
    /// </summary>
    public static byte[]? Decode(PdfDictionary stream, out IReadOnlyList<(string Name, PdfDictionary? Parameters)> remaining)
    {
        remaining = [];
        if (stream.Stream?.Value is not { } data || Filters(stream) is not { } filters)
        {
            return null;
        }

        var at = 0;
        try
        {
            for (; at < filters.Count && IsGeneral(filters[at].Name); at++)
            {
                var (name, parameters) = filters[at];
                if (parameters is not null && Integer(parameters, "/Predictor") is > 1 and < 10)
                {
                    return null; // the TIFF predictor, which PDFsharp does not undo
                }

                data = name == "/RunLengthDecode"
                    ? RunLength(data)
                    : Filtering.GetFilter(name)?.Decode(data, parameters is null ? new FilterParms(null) : new FilterParms(parameters));
                if (data is null)
                {
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // corrupt data: left as it is
        }

        remaining = [.. filters.Skip(at)];
        return data;
    }

    /// <summary>The data of a stream whose filters are all general-purpose, decoded; null otherwise.</summary>
    public static byte[]? Decode(PdfDictionary stream) => Decode(stream, out var remaining) is { } data && remaining.Count == 0 ? data : null;

    public static int? Integer(PdfDictionary dictionary, string key) => Resolve(dictionary.Elements[key]) switch
    {
        PdfInteger value => value.Value,
        PdfLongInteger value when value.Value is >= int.MinValue and <= int.MaxValue => (int)value.Value,
        PdfReal value when value.Value is >= int.MinValue and <= int.MaxValue && value.Value == Math.Floor(value.Value) => (int)value.Value,
        _ => null,
    };

    private static string? FullName(string name) => General.GetValueOrDefault(name) ?? Codecs.GetValueOrDefault(name);

    /// <summary>RunLengthDecode (7.4.5): a length byte n, then n + 1 bytes (n &lt; 128) or one byte 257 − n times; 128 ends.</summary>
    private static byte[] RunLength(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 2);
        var at = 0;
        while (at < data.Length)
        {
            var length = data[at++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - at);
                output.Write(data, at, count);
                at += count;
            }
            else if (at < data.Length)
            {
                var value = data[at++];
                for (var i = 0; i < 257 - length; i++)
                {
                    output.WriteByte(value);
                }
            }
        }

        return output.ToArray();
    }
}
