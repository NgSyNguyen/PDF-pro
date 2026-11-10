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
                if (name == "/LZWDecode")
                {
                    data = Lzw(data, parameters);
                }
                else if (parameters is not null && Integer(parameters, "/Predictor") is > 1 and < 10)
                {
                    return null; // the TIFF predictor, which PDFsharp does not undo
                }
                else
                {
                    data = name == "/RunLengthDecode"
                        ? RunLength(data)
                        : Filtering.GetFilter(name)?.Decode(data, parameters is null ? new FilterParms(null) : new FilterParms(parameters));
                }

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

    /// <summary>
    /// A stream's data decoded (content, CMaps, appearances): by <see cref="Decode(PdfDictionary)"/>, else as PDFsharp
    /// undoes it (which may throw on damaged data, as before). PDFsharp alone is not trusted with LZW.
    /// </summary>
    public static byte[] Bytes(PdfDictionary stream) => Decode(stream) ?? stream.Stream?.UnfilteredValue ?? [];

    /// <summary>
    /// LZWDecode (7.4.4) undone by PDF_Pro's own decoder — PDFsharp 6.2.4 loses bytes of some LZW data — then its
    /// predictor: PNG (10–15, a filter byte per row) or TIFF 2 (8 bits per component). Null when it cannot be undone.
    /// </summary>
    private static byte[]? Lzw(byte[] data, PdfDictionary? parameters)
    {
        var earlyChange = parameters is null || Integer(parameters, "/EarlyChange") is not 0;
        var decoded = PdfPro.Core.Imaging.TiffLzw.Decode(data, earlyChange);
        var predictor = parameters is null ? 1 : Integer(parameters, "/Predictor") ?? 1;
        if (predictor <= 1)
        {
            return decoded;
        }

        var colors = Math.Max(1, Integer(parameters!, "/Colors") ?? 1);
        var bits = Integer(parameters!, "/BitsPerComponent") ?? 8;
        var columns = Math.Max(1, Integer(parameters!, "/Columns") ?? 1);
        if (bits is not (1 or 2 or 4 or 8 or 16) || (long)colors * bits * columns > int.MaxValue / 2)
        {
            return null;
        }

        var row = ((colors * bits * columns) + 7) / 8;
        return predictor >= 10 ? UndoPng(decoded, row, Math.Max(1, colors * bits / 8)) : predictor == 2 && bits == 8 ? UndoTiff(decoded, row, colors) : null;
    }

    /// <summary>PNG predictors: each row starts with its filter (0 none, 1 sub, 2 up, 3 average, 4 Paeth).</summary>
    private static byte[] UndoPng(byte[] data, int row, int pixel)
    {
        var rows = data.Length / (row + 1);
        var output = new byte[rows * row];
        for (var y = 0; y < rows; y++)
        {
            var filter = data[y * (row + 1)];
            var source = (y * (row + 1)) + 1;
            var at = y * row;
            for (var x = 0; x < row; x++)
            {
                int left = x >= pixel ? output[at + x - pixel] : 0;
                int up = y > 0 ? output[at - row + x] : 0;
                int corner = x >= pixel && y > 0 ? output[at - row + x - pixel] : 0;
                var value = data[source + x];
                output[at + x] = (byte)(filter switch
                {
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) / 2),
                    4 => value + Paeth(left, up, corner),
                    _ => value,
                });
            }
        }

        return output;
    }

    private static int Paeth(int left, int up, int corner)
    {
        var estimate = left + up - corner;
        var (a, b, c) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - corner));
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    /// <summary>TIFF predictor 2 for 8-bit components: each sample after the first pixel of a row adds the one before it.</summary>
    private static byte[] UndoTiff(byte[] data, int row, int colors)
    {
        var output = (byte[])data.Clone();
        for (var start = 0; start + row <= output.Length; start += row)
        {
            for (var x = colors; x < row; x++)
            {
                output[start + x] += output[start + x - colors];
            }
        }

        return output;
    }

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
