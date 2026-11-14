using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tables;

/// <summary>How numbers written in a table's text are recognised, so a spreadsheet can count with them.</summary>
public enum NumberReading
{
    /// <summary>Every cell stays text.</summary>
    Text,

    /// <summary>"1.234.567,5" (Vietnamese, German…): dots group thousands, a comma marks decimals.</summary>
    DotGrouping,

    /// <summary>"1,234,567.5" (English): commas group thousands, a dot marks decimals.</summary>
    CommaGrouping,
}

/// <summary>A sheet to write: its name and its rows (the first one a header when <paramref name="HeaderRow"/>).</summary>
public sealed record SheetContent(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, bool HeaderRow = true);

/// <summary>Numbers in a cell's text, by the way they are written.</summary>
public static class TableNumbers
{
    /// <summary>The number a cell's text holds, written the given way ("1.250.000" → 1250000), or null (text).</summary>
    public static double? Parse(string text, NumberReading reading)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.Trim();
        if (reading == NumberReading.Text || value.Length is 0 or > 32)
        {
            return null;
        }

        var (group, point) = reading == NumberReading.DotGrouping ? ('.', ',') : (',', '.');
        var sign = value[0] is '-' or '+' ? value[..1] : string.Empty;
        var body = value[sign.Length..];
        var parts = body.Split(point);
        if (parts.Length > 2 || parts[0].Length == 0 || (parts.Length == 2 && (parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))))
        {
            return null;
        }

        var groups = parts[0].Split(group);
        if (!groups.All(g => g.Length > 0 && g.All(char.IsAsciiDigit))
            || (groups.Length > 1 && (groups[0].Length > 3 || groups.Skip(1).Any(g => g.Length != 3))))
        {
            return null;
        }

        // "007" is a code, not seven.
        if (groups.Length == 1 && groups[0].Length > 1 && groups[0][0] == '0')
        {
            return null;
        }

        var invariant = sign + string.Concat(groups) + (parts.Length == 2 ? "." + parts[1] : string.Empty);
        return double.TryParse(invariant, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
}

/// <summary>
/// Writes an Excel workbook (.xlsx, ECMA-376) with System.IO.Compression and XML alone: one sheet per table, text as
/// inline strings (NFC), numbers as numbers when asked (<see cref="NumberReading"/>), the header row bold and frozen,
/// columns as wide as their text. Sheet names are made valid for Excel (31 characters, no []:*?/\, unique).
/// </summary>
public static class XlsxWriter
{
    public const int MaxSheetName = 31;

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), CloseOutput = false };

    /// <summary>Writes the workbook to <paramref name="output"/> (left open).</summary>
    public static void Write(Stream output, IReadOnlyList<SheetContent> sheets, NumberReading numbers = NumberReading.Text)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            throw new ArgumentException("A workbook has at least one sheet.", nameof(sheets));
        }

        var names = SheetNames(sheets.Select(s => s.Name).ToList());
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", w => ContentTypes(w, sheets.Count));
        Part(zip, "_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", PackageRelationships);
            Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        Part(zip, "xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", Main);
            w.WriteAttributeString("xmlns", "r", null, Relationships);
            w.WriteStartElement("sheets", Main);
            for (var i = 0; i < names.Count; i++)
            {
                w.WriteStartElement("sheet", Main);
                w.WriteAttributeString("name", names[i]);
                w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("id", Relationships, $"rId{i + 1}");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
        });
        Part(zip, "xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", PackageRelationships);
            for (var i = 0; i < sheets.Count; i++)
            {
                Relationship(w, $"rId{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml");
            }

            Relationship(w, $"rId{sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            w.WriteEndElement();
        });
        Part(zip, "xl/styles.xml", Styles);
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            Part(zip, $"xl/worksheets/sheet{i + 1}.xml", w => Worksheet(w, sheet, numbers));
        }
    }

    /// <summary>Names Excel accepts: forbidden characters replaced, at most 31 characters, unique (ignoring case).</summary>
    public static IReadOnlyList<string> SheetNames(IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(wanted.Count);
        foreach (var raw in wanted)
        {
            var clean = new string([.. TextNormalizer.ToNfc(raw ?? string.Empty).Select(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' || char.IsControl(c) ? ' ' : c)]).Trim().Trim('\'');
            if (clean.Length == 0)
            {
                clean = "Sheet" + (names.Count + 1).ToString(CultureInfo.InvariantCulture);
            }

            var name = Cut(clean, MaxSheetName);
            for (var n = 2; !used.Add(name); n++)
            {
                var suffix = $" ({n.ToString(CultureInfo.InvariantCulture)})";
                name = Cut(clean, MaxSheetName - suffix.Length) + suffix;
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>"A" for 0, "Z" for 25, "AA" for 26.</summary>
    public static string ColumnName(int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        var name = string.Empty;
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    private static void Worksheet(XmlWriter w, SheetContent sheet, NumberReading numbers)
    {
        var columns = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count);
        w.WriteStartElement("worksheet", Main);
        if (sheet.HeaderRow && sheet.Rows.Count > 1)
        {
            w.WriteStartElement("sheetViews", Main);
            w.WriteStartElement("sheetView", Main);
            w.WriteAttributeString("workbookViewId", "0");
            w.WriteStartElement("pane", Main);
            w.WriteAttributeString("ySplit", "1");
            w.WriteAttributeString("topLeftCell", "A2");
            w.WriteAttributeString("activePane", "bottomLeft");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        if (columns > 0)
        {
            w.WriteStartElement("cols", Main);
            for (var c = 0; c < columns; c++)
            {
                var longest = sheet.Rows.Max(r => c < r.Count ? Longest(r[c]) : 0);
                var number = (c + 1).ToString(CultureInfo.InvariantCulture);
                w.WriteStartElement("col", Main);
                w.WriteAttributeString("min", number);
                w.WriteAttributeString("max", number);
                w.WriteAttributeString("width", Math.Clamp(longest + 2, 6, 60).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteStartElement("sheetData", Main);
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var header = sheet.HeaderRow && r == 0;
            var row = (r + 1).ToString(CultureInfo.InvariantCulture);
            w.WriteStartElement("row", Main);
            w.WriteAttributeString("r", row);
            for (var c = 0; c < sheet.Rows[r].Count; c++)
            {
                var text = Clean(sheet.Rows[r][c]);
                if (text.Length == 0)
                {
                    continue;
                }

                w.WriteStartElement("c", Main);
                w.WriteAttributeString("r", ColumnName(c) + row);
                if (header)
                {
                    w.WriteAttributeString("s", "1");
                }

                if (!header && TableNumbers.Parse(text, numbers) is { } number)
                {
                    w.WriteElementString("v", Main, number.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    w.WriteAttributeString("t", "inlineStr");
                    w.WriteStartElement("is", Main);
                    w.WriteStartElement("t", Main);
                    if (text != text.Trim() || text.Contains('\n', StringComparison.Ordinal))
                    {
                        w.WriteAttributeString("xml", "space", null, "preserve");
                    }

                    w.WriteString(text);
                    w.WriteEndElement();
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Styles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", Main);
        w.WriteStartElement("fonts", Main);
        w.WriteAttributeString("count", "2");
        Font(w, bold: false);
        Font(w, bold: true);
        w.WriteEndElement();
        w.WriteStartElement("fills", Main);
        w.WriteAttributeString("count", "2");
        foreach (var pattern in new[] { "none", "gray125" })
        {
            w.WriteStartElement("fill", Main);
            w.WriteStartElement("patternFill", Main);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteStartElement("borders", Main);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", Main);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            w.WriteElementString(side, Main, string.Empty);
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("cellStyleXfs", Main);
        w.WriteAttributeString("count", "1");
        Xf(w, font: 0, isStyle: true);
        w.WriteEndElement();
        w.WriteStartElement("cellXfs", Main);
        w.WriteAttributeString("count", "2");
        Xf(w, font: 0, isStyle: false);
        Xf(w, font: 1, isStyle: false);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Font(XmlWriter w, bool bold)
    {
        w.WriteStartElement("font", Main);
        if (bold)
        {
            w.WriteElementString("b", Main, string.Empty);
        }

        w.WriteStartElement("sz", Main);
        w.WriteAttributeString("val", "11");
        w.WriteEndElement();
        w.WriteStartElement("name", Main);
        w.WriteAttributeString("val", "Calibri");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Xf(XmlWriter w, int font, bool isStyle)
    {
        w.WriteStartElement("xf", Main);
        w.WriteAttributeString("numFmtId", "0");
        w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        if (!isStyle)
        {
            w.WriteAttributeString("xfId", "0");
            if (font != 0)
            {
                w.WriteAttributeString("applyFont", "1");
            }
        }

        w.WriteEndElement();
    }

    private static void ContentTypes(XmlWriter w, int sheets)
    {
        const string Types = "http://schemas.openxmlformats.org/package/2006/content-types";
        w.WriteStartElement("Types", Types);
        foreach (var (extension, type) in new[] { ("rels", "application/vnd.openxmlformats-package.relationships+xml"), ("xml", "application/xml") })
        {
            w.WriteStartElement("Default", Types);
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        Override(w, Types, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Override(w, Types, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        for (var i = 1; i <= sheets; i++)
        {
            Override(w, Types, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }

        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string ns, string part, string type)
    {
        w.WriteStartElement("Override", ns);
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", PackageRelationships);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    private static void Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, Settings);
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    /// <summary>NFC, without characters XML cannot hold (controls other than tab and line feed, lone surrogates).</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var nfc = TextNormalizer.ToNfc(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(nfc.Length);
        for (var i = 0; i < nfc.Length; i++)
        {
            var c = nfc[i];
            if (char.IsHighSurrogate(c) && i + 1 < nfc.Length && char.IsLowSurrogate(nfc[i + 1]))
            {
                builder.Append(c).Append(nfc[++i]);
            }
            else if (!char.IsSurrogate(c) && (c >= ' ' || c is '\t' or '\n') && c is not '￾' and not '￿')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static int Longest(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Max(l => l.Length);

    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var cut = text[..length];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }
}

/// <summary>
/// Writes a table as CSV (RFC 4180) for Excel: UTF-8 with a byte order mark (so Vietnamese opens right), CRLF, the
/// chosen separator, cells quoted when needed, and text that a spreadsheet would run as a formula ("=…", "@…", "+x")
/// prefixed with an apostrophe — numbers such as "-5" stay as they are.
/// </summary>
public static class CsvWriter
{
    public static byte[] Write(IReadOnlyList<IReadOnlyList<string>> rows, char separator = ',')
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (separator is '"' or '\r' or '\n')
        {
            throw new ArgumentOutOfRangeException(nameof(separator), separator, "Not a separator.");
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(separator);
                }

                builder.Append(Cell(row[i], separator));
            }

            builder.Append("\r\n");
        }

        var text = builder.ToString();
        var bytes = new byte[3 + Encoding.UTF8.GetByteCount(text)];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 3);
        return bytes;
    }

    private static string Cell(string? value, char separator)
    {
        var text = TextNormalizer.ToNfc(value ?? string.Empty);
        if (text.Length > 0 && (text[0] is '=' or '@' or '\t' or '\r' || (text[0] is '+' or '-' && !IsNumber(text))))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || text != text.Trim()
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    private static bool IsNumber(string text)
        => TableNumbers.Parse(text, NumberReading.DotGrouping) is not null || TableNumbers.Parse(text, NumberReading.CommaGrouping) is not null;
}
