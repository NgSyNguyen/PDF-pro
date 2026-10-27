using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tables;

/// <summary>A table that cannot be read (not CSV or a workbook, empty, or too big).</summary>
public sealed class TableFormatException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// A table of text read from a data file: the first row names the columns (blank or repeated names are made unique),
/// every other row has one cell per column. Text is NFC.
/// </summary>
public sealed class TabularData
{
    public const int MaxRows = 10_000;
    public const int MaxColumns = 500;
    public const int MaxCellLength = 20_000;

    public TabularData(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>The column of a name (exactly, else ignoring case), or -1.</summary>
    public int IndexOf(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var exact = Columns.ToList().IndexOf(column);
        return exact >= 0 ? exact : Columns.ToList().FindIndex(c => string.Equals(c, TextNormalizer.ToNfc(column), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds a table from raw rows (the first one names the columns); empty rows are left out.</summary>
    /// <exception cref="TableFormatException">There is no header row, or the table is too big.</exception>
    public static TabularData FromRows(IReadOnlyList<IReadOnlyList<string>> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var rows = raw.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count == 0)
        {
            throw new TableFormatException("The table is empty.");
        }

        if (rows.Count - 1 > MaxRows || rows[0].Count > MaxColumns)
        {
            throw new TableFormatException($"A table has at most {MaxRows} rows and {MaxColumns} columns.");
        }

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows[0].Count; i++)
        {
            var name = TextNormalizer.ToNfc(rows[0][i].Trim());
            if (name.Length == 0)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"#{i + 1}");
            }

            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            }

            columns.Add(unique);
        }

        var body = rows.Skip(1)
            .Select(r => (IReadOnlyList<string>)[.. Enumerable.Range(0, columns.Count).Select(i => i < r.Count ? Cell(r[i]) : string.Empty)])
            .ToList();
        return new TabularData(columns, body);
    }

    private static string Cell(string text)
    {
        var cell = TextNormalizer.ToNfc(text);
        return cell.Length > MaxCellLength ? cell[..MaxCellLength] : cell;
    }
}

/// <summary>
/// CSV (RFC 4180) as written by Excel and other programs: the separator (comma, semicolon or tab) is the one the header
/// line uses; quoted cells may hold separators, quotes ("") and line breaks. Bytes are UTF-8 (with or without BOM) or
/// UTF-16 with a BOM; anything else is read as Windows-1258, the Vietnamese code page of older Excel versions.
/// </summary>
public static class CsvTable
{
    /// <exception cref="TableFormatException">There is nothing to read, or the table is too big.</exception>
    public static TabularData Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(Decode(bytes));
    }

    /// <exception cref="TableFormatException">There is nothing to read, or the table is too big.</exception>
    public static TabularData Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var separator = Separator(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                if (rows.Count > TabularData.MaxRows + 1)
                {
                    throw new TableFormatException($"A table has at most {TabularData.MaxRows} rows.");
                }
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return TabularData.FromRows(rows);
    }

    /// <summary>The text of a CSV file (see the class), NFC.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return TextNormalizer.ToNfc(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }

        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return TextNormalizer.ToNfc(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }

        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return TextNormalizer.ToNfc(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        }

        try
        {
            return TextNormalizer.ToNfc(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return TextNormalizer.ToNfc(Encoding.GetEncoding(1258).GetString(bytes));
        }
    }

    /// <summary>The separator the first line uses most (outside quotes): comma, semicolon or tab.</summary>
    private static char Separator(string text)
    {
        int comma = 0, semicolon = 0, tab = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted)
            {
                comma += c == ',' ? 1 : 0;
                semicolon += c == ';' ? 1 : 0;
                tab += c == '\t' ? 1 : 0;
            }
        }

        return tab > comma && tab >= semicolon ? '\t' : semicolon > comma ? ';' : ',';
    }
}

/// <summary>
/// The first sheet of an Excel workbook (.xlsx, Office Open XML), read without Excel or any package: shared and
/// inline text, numbers (as plain numbers, "1234.5"), dates (cells with a date format, as dd/MM/yyyy), true/false.
/// Formulas give their last computed value. XML is read without DTDs; sizes are bounded.
/// </summary>
public static class XlsxTable
{
    /// <summary>The largest part read from the workbook (bytes, uncompressed).</summary>
    private const long MaxPartBytes = 200L * 1024 * 1024;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <exception cref="TableFormatException">The file is not a workbook, or the sheet is too big.</exception>
    public static TabularData Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var strings = SharedStrings(zip);
            var dates = DateStyles(zip);
            var sheet = Load(zip, FirstSheetPath(zip)) ?? throw new TableFormatException("The workbook has no sheet.");
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in sheet.Descendants(Main + "row"))
            {
                if (rows.Count > TabularData.MaxRows + 1)
                {
                    throw new TableFormatException($"A table has at most {TabularData.MaxRows} rows.");
                }

                var cells = new List<string>();
                var next = 0;
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = (string?)cell.Attribute("r") is { } reference ? ColumnOf(reference) : next;
                    if (column >= TabularData.MaxColumns)
                    {
                        continue;
                    }

                    while (cells.Count < column)
                    {
                        cells.Add(string.Empty);
                    }

                    cells.Add(Value(cell, strings, dates));
                    next = column + 1;
                }

                rows.Add(cells);
            }

            return TabularData.FromRows(rows);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException)
        {
            throw new TableFormatException("The workbook cannot be read.", ex);
        }
    }

    private static string Value(XElement cell, IReadOnlyList<string> strings, IReadOnlySet<int> dates)
    {
        var type = (string?)cell.Attribute("t");
        var raw = cell.Element(Main + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < strings.Count ? strings[index] : string.Empty;
            case "inlineStr":
                return Text(cell.Element(Main + "is"));
            case "b":
                return raw == "1" ? "true" : "false";
            case "str" or "e":
                return raw ?? string.Empty;
        }

        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw ?? string.Empty;
        }

        if ((int?)cell.Attribute("s") is { } style && dates.Contains(style) && number is > -657435 and < 2958466)
        {
            var date = DateTime.FromOADate(number);
            return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>"AB12" → 27 (0-based column).</summary>
    private static int ColumnOf(string reference)
    {
        var column = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
        {
            column = checked((column * 26) + (char.ToUpperInvariant(c) - 'A' + 1));
        }

        return column - 1;
    }

    private static List<string> SharedStrings(ZipArchive zip)
        => Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(Main + "si").Select(Text).ToList() ?? [];

    /// <summary>The text of a string item: plain &lt;t&gt; or the runs of rich text (phonetic hints left out).</summary>
    private static string Text(XElement? item)
        => item is null ? string.Empty : string.Concat(item.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value));

    /// <summary>The cell styles whose number format shows a date.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        if (Load(zip, "xl/styles.xml")?.Root is not { } styles)
        {
            return result;
        }

        var custom = styles.Element(Main + "numFmts")?.Elements(Main + "numFmt")
            .Where(f => IsDateFormat((string?)f.Attribute("formatCode") ?? string.Empty))
            .Select(f => (int?)f.Attribute("numFmtId") ?? -1)
            .ToHashSet() ?? [];
        var index = 0;
        foreach (var xf in styles.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.Contains(id))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    /// <summary>A custom number format showing a date: day, month or year letters outside quotes and brackets.</summary>
    private static bool IsDateFormat(string code)
    {
        var plain = new StringBuilder();
        var skip = '\0';
        foreach (var c in code)
        {
            if (skip != '\0')
            {
                skip = c == skip ? '\0' : skip;
            }
            else if (c is '"')
            {
                skip = '"';
            }
            else if (c is '[')
            {
                skip = ']';
            }
            else
            {
                plain.Append(char.ToLowerInvariant(c));
            }
        }

        var text = plain.ToString();
        return text.Contains('d') || text.Contains('y') || (text.Contains('m') && !text.Contains('h') && !text.Contains('s'));
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml")?.Root ?? throw new TableFormatException("The file is not an Excel workbook.");
        var id = (string?)workbook.Element(Main + "sheets")?.Elements(Main + "sheet").FirstOrDefault()?.Attribute(Relationships + "id");
        var target = Load(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(r => (string?)r.Attribute("Id") == id)?.Attribute("Target")?.Value;
        if (target is null)
        {
            return "xl/worksheets/sheet1.xml";
        }

        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not { } entry)
        {
            return null;
        }

        if (entry.Length > MaxPartBytes)
        {
            throw new TableFormatException("The workbook is too big.");
        }

        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPartBytes };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }
}
