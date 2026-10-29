using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPro.Operations.Optimizing;

/// <summary>
/// Rebuilds the cross-reference section of a file whose objects are intact but whose cross-reference table or stream
/// and trailer are lost or wrong (a truncated download, a bad tool): the objects are found by scanning for
/// "n g obj" — and inside object streams — the catalog becomes /Root, the document information /Info, and a new
/// cross-reference section with its trailer is appended. Every byte of the file stays as it was.
/// </summary>
public static partial class CrossReferenceRebuilder
{
    /// <summary>Object streams larger than this (decompressed) are not looked into.</summary>
    private const int MaxObjectStreamBytes = 64 << 20;

    /// <summary>The file with a new cross-reference section, or null when no catalog was found (nothing to rebuild on).</summary>
    public static byte[]? Rebuild(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var text = Encoding.Latin1.GetString(file);

        // Plain objects, the last of each number winning (as the newest revision would).
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        var bodies = new Dictionary<int, string>();
        foreach (Match header in ObjectHeader().Matches(text))
        {
            if (!int.TryParse(header.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || !int.TryParse(header.Groups["g"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                || number <= 0 || number > 8_388_607)
            {
                continue;
            }

            var start = header.Index + header.Length;
            var end = text.IndexOf("endobj", start, StringComparison.Ordinal);
            offsets[number] = (header.Index, generation);
            bodies[number] = end < 0 ? text[start..Math.Min(text.Length, start + 4096)] : text[start..end];
        }

        // Objects kept in object streams: number → (stream, index).
        var compressed = new SortedDictionary<int, (int Stream, int Index)>();
        var compressedBodies = new Dictionary<int, string>();
        foreach (var (number, body) in bodies)
        {
            if (TypeName().Match(body) is { Success: true } type && type.Groups[1].Value == "ObjStm")
            {
                foreach (var (inner, index, innerBody) in ObjectStream(body))
                {
                    if (!offsets.ContainsKey(inner))
                    {
                        compressed[inner] = (number, index);
                        compressedBodies[inner] = innerBody;
                    }
                }
            }
        }

        int? Find(Func<string, bool> matches)
            => bodies.Concat(compressedBodies).Where(b => matches(b.Value)).Select(b => (int?)b.Key).LastOrDefault();

        var root = Find(body => TypeName().Matches(body).Any(m => m.Groups[1].Value == "Catalog") && body.Contains("/Pages", StringComparison.Ordinal));
        if (root is null)
        {
            return null;
        }

        var info = Find(body => !body.Contains("/Type", StringComparison.Ordinal) && !body.Contains("stream", StringComparison.Ordinal)
            && (body.Contains("/Producer", StringComparison.Ordinal) || body.Contains("/CreationDate", StringComparison.Ordinal) || body.Contains("/Creator", StringComparison.Ordinal)));
        var size = Math.Max(offsets.Keys.DefaultIfEmpty(0).Max(), compressed.Keys.DefaultIfEmpty(0).Max()) + 1;

        using var output = new MemoryStream(file.Length + (size * 20) + 256);
        output.Write(file);
        if (file.Length == 0 || file[^1] is not ((byte)'\n' or (byte)'\r'))
        {
            output.WriteByte((byte)'\n');
        }

        var trailer = new StringBuilder();
        trailer.Append(CultureInfo.InvariantCulture, $"/Root {root} {Generation(root.Value)} R");
        if (info is { } infoNumber)
        {
            trailer.Append(CultureInfo.InvariantCulture, $" /Info {infoNumber} {Generation(infoNumber)} R");
        }

        var xrefAt = output.Position;
        if (compressed.Count == 0)
        {
            // A classic table: every number from 0 to the highest, free where nothing was found.
            var table = new StringBuilder("xref\n");
            table.Append(CultureInfo.InvariantCulture, $"0 {size}\n");
            table.Append("0000000000 65535 f\r\n");
            for (var number = 1; number < size; number++)
            {
                table.Append(offsets.TryGetValue(number, out var entry)
                    ? string.Create(CultureInfo.InvariantCulture, $"{entry.Offset:D10} {entry.Generation:D5} n\r\n")
                    : "0000000000 00000 f\r\n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} {trailer} >>\nstartxref\n{xrefAt}\n%%EOF\n");
            Write(output, table.ToString());
        }
        else
        {
            // A cross-reference stream (type 2 entries point into object streams), itself the last object.
            var self = size;
            var rows = new byte[(self + 1) * 7];
            for (var number = 0; number <= self; number++)
            {
                var row = rows.AsSpan(number * 7, 7);
                if (number == self)
                {
                    Entry(row, 1, xrefAt, 0);
                }
                else if (offsets.TryGetValue(number, out var plain))
                {
                    Entry(row, 1, plain.Offset, plain.Generation);
                }
                else if (compressed.TryGetValue(number, out var inner))
                {
                    Entry(row, 2, inner.Stream, inner.Index);
                }
                else
                {
                    Entry(row, 0, 0, number == 0 ? 65535 : 0);
                }
            }

            Write(output, string.Create(CultureInfo.InvariantCulture, $"{self} 0 obj\n<< /Type /XRef /Size {self + 1} /W [1 4 2] /Length {rows.Length} {trailer} >>\nstream\n"));
            output.Write(rows);
            Write(output, string.Create(CultureInfo.InvariantCulture, $"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n"));
        }

        return output.ToArray();

        int Generation(int number) => offsets.TryGetValue(number, out var entry) ? entry.Generation : 0;
    }

    /// <summary>The objects an object stream holds: number, index and text.</summary>
    private static IEnumerable<(int Number, int Index, string Body)> ObjectStream(string body)
    {
        var dictionaryEnd = body.IndexOf("stream", StringComparison.Ordinal);
        if (dictionaryEnd < 0 || IntegerEntry(body[..dictionaryEnd], "N") is not { } count || IntegerEntry(body[..dictionaryEnd], "First") is not { } first)
        {
            yield break;
        }

        var dataStart = dictionaryEnd + "stream".Length;
        if (dataStart < body.Length && body[dataStart] == '\r')
        {
            dataStart++;
        }

        if (dataStart < body.Length && body[dataStart] == '\n')
        {
            dataStart++;
        }

        var dataEnd = body.LastIndexOf("endstream", StringComparison.Ordinal);
        if (dataEnd <= dataStart)
        {
            yield break;
        }

        string data;
        try
        {
            var raw = Encoding.Latin1.GetBytes(body[dataStart..dataEnd]);
            if (!body[..dictionaryEnd].Contains("/FlateDecode", StringComparison.Ordinal))
            {
                yield break;
            }

            using var input = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                decoded.Write(buffer, 0, read);
                if (decoded.Length > MaxObjectStreamBytes)
                {
                    yield break;
                }
            }

            data = Encoding.Latin1.GetString(decoded.ToArray());
        }
        catch (InvalidDataException)
        {
            yield break;
        }

        var header = Numbers().Matches(data[..Math.Min(first, data.Length)]).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        var pairs = Math.Min(count, header.Count / 2);
        for (var i = 0; i < pairs; i++)
        {
            var start = first + header[(2 * i) + 1];
            var end = i + 1 < pairs ? first + header[(2 * i) + 3] : data.Length;
            if (start >= 0 && start <= end && end <= data.Length)
            {
                yield return (header[2 * i], i, data[start..end]);
            }
        }
    }

    private static int? IntegerEntry(string dictionary, string key)
        => Regex.Match(dictionary, $@"/{key}\s+(\d+)") is { Success: true } match && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static void Entry(Span<byte> row, int type, long field2, int field3)
    {
        row[0] = (byte)type;
        row[1] = (byte)(field2 >> 24);
        row[2] = (byte)(field2 >> 16);
        row[3] = (byte)(field2 >> 8);
        row[4] = (byte)field2;
        row[5] = (byte)(field3 >> 8);
        row[6] = (byte)field3;
    }

    private static void Write(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    [GeneratedRegex(@"(?<![0-9])(?<n>\d{1,10})[ \t\r\n\f\0]+(?<g>\d{1,5})[ \t\r\n\f\0]+obj(?![A-Za-z])")]
    private static partial Regex ObjectHeader();

    [GeneratedRegex(@"/Type\s*/(\w+)")]
    private static partial Regex TypeName();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}
