using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPro.Operations.Signing;

/// <summary>What the last cross-reference section of a file says (7.5.5, 7.5.8).</summary>
/// <param name="StartXref">Where that section starts.</param>
/// <param name="IsStream">The section is a cross-reference stream (then updates are written as streams too).</param>
/// <param name="Size">One more than the highest object number.</param>
/// <param name="Root">The catalog, as "n g R".</param>
/// <param name="Info">The document information, as "n g R", or null.</param>
/// <param name="Id">The file identifier array as written, or null.</param>
/// <param name="IsEncrypted">The file has an /Encrypt entry.</param>
internal sealed record FileTrailer(long StartXref, bool IsStream, int Size, string Root, string? Info, string? Id, bool IsEncrypted);

/// <summary>An object of an incremental update: its number, generation and body (what goes between "obj" and "endobj").</summary>
internal sealed record UpdatedObject(int Number, int Generation, byte[] Body);

/// <summary>
/// Incremental updates (7.5.6): new and changed objects appended after the file, with a cross-reference section of
/// their own and a trailer whose /Prev points at the previous one — every byte before stays as it was, which is what
/// keeps earlier signatures valid.
/// </summary>
internal static partial class IncrementalUpdate
{
    /// <summary>Reads the trailer of the last cross-reference section.</summary>
    /// <exception cref="InvalidDataException">The end of the file is not laid out as a PDF's.</exception>
    public static FileTrailer ReadTrailer(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tail = Encoding.Latin1.GetString(file, Math.Max(0, file.Length - 2048), Math.Min(2048, file.Length));
        var starts = StartXref().Matches(tail);
        if (starts.Count == 0 || !long.TryParse(starts[^1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startXref) || startXref >= file.Length)
        {
            throw new InvalidDataException("The file has no cross-reference section to update.");
        }

        var section = Encoding.Latin1.GetString(file, (int)startXref, (int)Math.Min(file.Length - startXref, 1 << 20));
        string dictionary;
        bool isStream;
        if (section.StartsWith("xref", StringComparison.Ordinal))
        {
            var trailer = section.IndexOf("trailer", StringComparison.Ordinal);
            dictionary = trailer < 0 ? throw new InvalidDataException("The cross-reference table has no trailer.") : Dictionary(section, section.IndexOf("<<", trailer, StringComparison.Ordinal));
            isStream = false;
        }
        else if (ObjectHeader().Match(section) is { Success: true, Index: 0 } header)
        {
            dictionary = Dictionary(section, section.IndexOf("<<", header.Length, StringComparison.Ordinal));
            isStream = true;
        }
        else
        {
            throw new InvalidDataException("The cross-reference section cannot be read.");
        }

        var size = SizeEntry().Match(dictionary);
        var root = RootEntry().Match(dictionary);
        if (!size.Success || !root.Success)
        {
            throw new InvalidDataException("The trailer has no /Size or /Root.");
        }

        var info = InfoEntry().Match(dictionary);
        var id = IdEntry().Match(dictionary);
        return new FileTrailer(
            startXref,
            isStream,
            int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
            root.Groups[1].Value,
            info.Success ? info.Groups[1].Value : null,
            id.Success ? id.Value[3..].Trim() : null,
            dictionary.Contains("/Encrypt", StringComparison.Ordinal));
    }

    /// <summary>
    /// The file with the objects appended, then a cross-reference section (a table, or a stream when the file uses
    /// them) and a trailer pointing back at the previous one. <paramref name="offsets"/> tells where each object starts.
    /// </summary>
    public static byte[] Append(byte[] file, FileTrailer trailer, IReadOnlyList<UpdatedObject> objects, out Dictionary<int, long> offsets)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(objects);
        using var output = new MemoryStream(file.Length + objects.Sum(o => o.Body.Length) + 4096);
        output.Write(file);
        if (file.Length == 0 || file[^1] is not ((byte)'\n' or (byte)'\r'))
        {
            output.WriteByte((byte)'\n');
        }

        offsets = [];
        foreach (var item in objects)
        {
            offsets[item.Number] = output.Position;
            Write(output, $"{item.Number} {item.Generation} obj\n");
            output.Write(item.Body);
            Write(output, "\nendobj\n");
        }

        var size = Math.Max(trailer.Size, objects.Count == 0 ? 0 : objects.Max(o => o.Number) + 1);
        var common = new StringBuilder();
        common.Append(CultureInfo.InvariantCulture, $"/Root {trailer.Root}");
        if (trailer.Info is { } info)
        {
            common.Append(CultureInfo.InvariantCulture, $" /Info {info}");
        }

        if (trailer.Id is { } id)
        {
            common.Append(CultureInfo.InvariantCulture, $" /ID {id}");
        }

        common.Append(CultureInfo.InvariantCulture, $" /Prev {trailer.StartXref}");
        var generations = objects.ToDictionary(o => o.Number, o => o.Generation);
        long xrefAt;
        if (!trailer.IsStream)
        {
            xrefAt = output.Position;
            var table = new StringBuilder("xref\n");
            foreach (var run in Runs(offsets.Keys))
            {
                table.Append(CultureInfo.InvariantCulture, $"{run[0]} {run.Count}\n");
                foreach (var number in run)
                {
                    table.Append(CultureInfo.InvariantCulture, $"{offsets[number]:D10} {generations[number]:D5} n\r\n");
                }
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} {common} >>\n");
            Write(output, table.ToString());
        }
        else
        {
            // A cross-reference stream of its own: type 1 entries (offset, generation), itself included.
            var self = size;
            xrefAt = output.Position;
            offsets[self] = xrefAt;
            generations[self] = 0;
            var runs = Runs(offsets.Keys);
            using var entries = new MemoryStream();
            foreach (var number in runs.SelectMany(r => r))
            {
                var offset = offsets[number];
                entries.WriteByte(1);
                entries.Write([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
                entries.Write([(byte)(generations[number] >> 8), (byte)generations[number]]);
            }

            var index = string.Join(' ', runs.Select(r => $"{r[0]} {r.Count}"));
            Write(output, $"{self} 0 obj\n<< /Type /XRef /Size {self + 1} /W [1 4 2] /Index [{index}] {common} /Length {entries.Length} >>\nstream\n");
            output.Write(entries.ToArray());
            Write(output, "\nendstream\nendobj\n");
        }

        Write(output, $"startxref\n{xrefAt}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>Consecutive object numbers grouped (one cross-reference subsection each).</summary>
    private static List<List<int>> Runs(IEnumerable<int> numbers)
    {
        var runs = new List<List<int>>();
        foreach (var number in numbers.Order())
        {
            if (runs.Count > 0 && runs[^1][^1] == number - 1)
            {
                runs[^1].Add(number);
            }
            else
            {
                runs.Add([number]);
            }
        }

        return runs;
    }

    /// <summary>The dictionary opened at <paramref name="open"/>, nested dictionaries and strings included.</summary>
    private static string Dictionary(string text, int open)
    {
        if (open < 0)
        {
            throw new InvalidDataException("The trailer has no dictionary.");
        }

        var depth = 0;
        for (var i = open; i < text.Length - 1; i++)
        {
            if (text[i] == '(')
            {
                for (var nesting = 0; i < text.Length; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                    else if (text[i] == '(')
                    {
                        nesting++;
                    }
                    else if (text[i] == ')' && --nesting == 0)
                    {
                        break;
                    }
                }
            }
            else if (text[i] == '<' && text[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (text[i] == '>' && text[i + 1] == '>')
            {
                if (--depth == 0)
                {
                    return text[open..(i + 2)];
                }

                i++;
            }
        }

        throw new InvalidDataException("The trailer dictionary is not closed.");
    }

    private static void Write(Stream output, string text) => output.Write(Encoding.Latin1.GetBytes(text));

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"^\d+\s+\d+\s+obj\b")]
    private static partial Regex ObjectHeader();

    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeEntry();

    [GeneratedRegex(@"/Root\s+(\d+\s+\d+\s+R)")]
    private static partial Regex RootEntry();

    [GeneratedRegex(@"/Info\s+(\d+\s+\d+\s+R)")]
    private static partial Regex InfoEntry();

    [GeneratedRegex(@"/ID\s*\[[^\]]*\]")]
    private static partial Regex IdEntry();
}
