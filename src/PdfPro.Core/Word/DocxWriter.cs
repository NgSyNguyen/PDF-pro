using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PdfPro.Core.Word;

/// <summary>How a paragraph lines up.</summary>
public enum DocxAlignment
{
    Left,
    Center,
    Right,

    /// <summary>Both edges straight (the last line left).</summary>
    Justify,
}

/// <summary>A piece of text in one style.</summary>
/// <param name="Text">The text (NFC is applied when written).</param>
/// <param name="Font">The font family ("Times New Roman"); null for the document's.</param>
/// <param name="Size">Its size in points.</param>
/// <param name="Bold">Bold.</param>
/// <param name="Italic">Italic.</param>
/// <param name="Color">"RRGGBB"; null for automatic (black).</param>
public sealed record DocxRun(string Text, string? Font = null, double Size = 12, bool Bold = false, bool Italic = false, string? Color = null);

/// <summary>Something the body of the document holds, in order.</summary>
public abstract record DocxBlock;

/// <summary>A paragraph.</summary>
/// <param name="Runs">Its text, piece by piece.</param>
/// <param name="Alignment">How it lines up.</param>
/// <param name="HeadingLevel">1–3 for a heading (Word's "Heading n": navigation pane, table of contents); 0 for body text.</param>
/// <param name="SpaceBefore">Space above it, in points.</param>
/// <param name="LeftIndent">Indent of every line, in points.</param>
/// <param name="FirstLineIndent">More indent of the first line, in points.</param>
public sealed record DocxParagraph(
    IReadOnlyList<DocxRun> Runs,
    DocxAlignment Alignment = DocxAlignment.Left,
    int HeadingLevel = 0,
    double SpaceBefore = 0,
    double LeftIndent = 0,
    double FirstLineIndent = 0) : DocxBlock;

/// <summary>A table with a grid: rows of cells, the shorter rows filled with empty cells; numbers to the right.</summary>
/// <param name="Rows">The text of each cell, row by row.</param>
/// <param name="Font">The font of its text; null for the document's.</param>
/// <param name="Size">Its size in points.</param>
/// <param name="ColumnWidths">Each column's share of the width (any unit); null for equal columns.</param>
public sealed record DocxTable(IReadOnlyList<IReadOnlyList<string>> Rows, string? Font = null, double Size = 11, IReadOnlyList<double>? ColumnWidths = null) : DocxBlock;

/// <summary>A picture on a line of its own, made smaller to fit between the margins when it is wider.</summary>
/// <param name="Data">A PNG or JPEG file.</param>
/// <param name="IsJpeg">JPEG (else PNG).</param>
/// <param name="Width">Its width in points.</param>
/// <param name="Height">Its height in points.</param>
/// <param name="Alignment">How it lines up.</param>
public sealed record DocxPicture(byte[] Data, bool IsJpeg, double Width, double Height, DocxAlignment Alignment = DocxAlignment.Center) : DocxBlock;

/// <summary>What follows starts on a new page.</summary>
public sealed record DocxPageBreak : DocxBlock;

/// <summary>A column of <see cref="DocxColumns"/>.</summary>
/// <param name="Width">Its share of the width (any unit: the columns are scaled to fit between the margins).</param>
/// <param name="Paragraphs">Its paragraphs, top to bottom.</param>
public sealed record DocxColumn(double Width, IReadOnlyList<DocxParagraph> Paragraphs);

/// <summary>
/// Text set side by side — the two halves of a Vietnamese administrative heading, columns — as a table without
/// borders of one row.
/// </summary>
/// <param name="Columns">The columns, left to right.</param>
public sealed record DocxColumns(IReadOnlyList<DocxColumn> Columns) : DocxBlock;

/// <summary>The size of the pages and their margins, in points.</summary>
public sealed record DocxPageSetup(double Width, double Height, double Top = 72, double Right = 72, double Bottom = 72, double Left = 72)
{
    /// <summary>A4 portrait, margins of 2 cm and 3 cm on the left (as Vietnamese administrative documents).</summary>
    public static DocxPageSetup A4 { get; } = new(595.3, 841.9, 56.7, 56.7, 56.7, 85);

    /// <summary>The width text and pictures may take.</summary>
    public double ContentWidth => Math.Max(72, Width - Left - Right);
}

/// <summary>A document to write.</summary>
/// <param name="Blocks">Its body, in order.</param>
/// <param name="Page">Its pages.</param>
/// <param name="Font">The font of text that does not name one.</param>
/// <param name="Size">Its size in points.</param>
/// <param name="Title">The title in the document's properties; null for none.</param>
public sealed record DocxContent(IReadOnlyList<DocxBlock> Blocks, DocxPageSetup Page, string Font = "Times New Roman", double Size = 12, string? Title = null);

/// <summary>
/// Writes a Word document (.docx, Office Open XML WordprocessingML) without a library: the parts a word processor
/// needs — the body, styles ("Heading 1–3" with their outline levels, a table grid style), pictures in word/media,
/// the title in the core properties (and nothing about who made it). Text is NFC; characters XML cannot hold are left
/// out. Elements are written in the order the schema asks for, which Microsoft Word checks.
/// </summary>
public static class DocxWriter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Twentieths of a point (twips) per point.</summary>
    private const int Twips = 20;

    /// <summary>English Metric Units per point (pictures).</summary>
    private const long Emu = 12700;

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), CloseOutput = false };

    /// <summary>Writes <paramref name="content"/> as a .docx into <paramref name="output"/>.</summary>
    public static void Write(Stream output, DocxContent content)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(content);
        var pictures = content.Blocks.OfType<DocxPicture>().ToList();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", w => ContentTypes(w, pictures));
        Part(zip, "_rels/.rels", PackageRels);
        Part(zip, "docProps/core.xml", w => CoreProperties(w, content.Title));
        Part(zip, "word/_rels/document.xml.rels", w => DocumentRels(w, pictures));
        Part(zip, "word/styles.xml", w => Styles(w, content));
        Part(zip, "word/document.xml", w => Document(w, content, pictures));
        for (var i = 0; i < pictures.Count; i++)
        {
            using var stream = zip.CreateEntry($"word/media/image{i + 1}{(pictures[i].IsJpeg ? ".jpeg" : ".png")}", CompressionLevel.NoCompression).Open();
            stream.Write(pictures[i].Data);
        }
    }

    /// <summary>Text XML can hold, in NFC (control characters but tab, line feed and carriage return are left out).</summary>
    internal static string Clean(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var clean = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (char.IsHighSurrogate(c) && i + 1 < normalized.Length && char.IsLowSurrogate(normalized[i + 1]))
            {
                clean.Append(c).Append(normalized[++i]);
            }
            else if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    private static void Part(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, Settings);
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void ContentTypes(XmlWriter w, IReadOnlyList<DocxPicture> pictures)
    {
        const string Ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        w.WriteStartElement("Types", Ns);
        Default(w, Ns, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(w, Ns, "xml", "application/xml");
        if (pictures.Any(p => !p.IsJpeg))
        {
            Default(w, Ns, "png", "image/png");
        }

        if (pictures.Any(p => p.IsJpeg))
        {
            Default(w, Ns, "jpeg", "image/jpeg");
        }

        Override(w, Ns, "/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        Override(w, Ns, "/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
        Override(w, Ns, "/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        w.WriteEndElement();

        static void Default(XmlWriter w, string ns, string extension, string type)
        {
            w.WriteStartElement("Default", ns);
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        static void Override(XmlWriter w, string ns, string part, string type)
        {
            w.WriteStartElement("Override", ns);
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }
    }

    private static void PackageRels(XmlWriter w)
    {
        w.WriteStartElement("Relationships", PackageRelationships);
        Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml");
        Relationship(w, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        w.WriteEndElement();
    }

    private static void DocumentRels(XmlWriter w, IReadOnlyList<DocxPicture> pictures)
    {
        w.WriteStartElement("Relationships", PackageRelationships);
        Relationship(w, "rIdStyles", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
        for (var i = 0; i < pictures.Count; i++)
        {
            Relationship(w, $"rIdImage{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", $"media/image{i + 1}{(pictures[i].IsJpeg ? ".jpeg" : ".png")}");
        }

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

    private static void CoreProperties(XmlWriter w, string? title)
    {
        const string Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        const string Dc = "http://purl.org/dc/elements/1.1/";
        w.WriteStartElement("cp", "coreProperties", Cp);
        w.WriteAttributeString("xmlns", "dc", null, Dc);
        if (!string.IsNullOrWhiteSpace(title))
        {
            w.WriteElementString("dc", "title", Dc, Clean(title));
        }

        w.WriteEndElement();
    }

    private static void Styles(XmlWriter w, DocxContent content)
    {
        w.WriteStartElement("w", "styles", W);
        w.WriteStartElement("w", "docDefaults", W);
        w.WriteStartElement("w", "rPrDefault", W);
        w.WriteStartElement("w", "rPr", W);
        Fonts(w, content.Font);
        Value(w, "sz", HalfPoints(content.Size));
        Value(w, "szCs", HalfPoints(content.Size));
        w.WriteStartElement("w", "lang", W);
        w.WriteAttributeString("w", "val", W, "vi-VN");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("w", "pPrDefault", W);
        w.WriteStartElement("w", "pPr", W);
        w.WriteStartElement("w", "spacing", W);
        w.WriteAttributeString("w", "after", W, "0");
        w.WriteAttributeString("w", "line", W, "264");
        w.WriteAttributeString("w", "lineRule", W, "auto");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("w", "style", W);
        w.WriteAttributeString("w", "type", W, "paragraph");
        w.WriteAttributeString("w", "default", W, "1");
        w.WriteAttributeString("w", "styleId", W, "Normal");
        Value(w, "name", "Normal");
        Element(w, "qFormat");
        w.WriteEndElement();

        for (var level = 1; level <= 3; level++)
        {
            w.WriteStartElement("w", "style", W);
            w.WriteAttributeString("w", "type", W, "paragraph");
            w.WriteAttributeString("w", "styleId", W, $"Heading{level}");
            Value(w, "name", $"heading {level}");
            Value(w, "basedOn", "Normal");
            Value(w, "next", "Normal");
            Element(w, "qFormat");
            w.WriteStartElement("w", "pPr", W);
            Element(w, "keepNext");
            Value(w, "outlineLvl", (level - 1).ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteStartElement("w", "rPr", W);
            Element(w, "b");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteStartElement("w", "style", W);
        w.WriteAttributeString("w", "type", W, "table");
        w.WriteAttributeString("w", "styleId", W, "TableGrid");
        Value(w, "name", "Table Grid");
        w.WriteStartElement("w", "tblPr", W);
        w.WriteStartElement("w", "tblBorders", W);
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            w.WriteStartElement("w", side, W);
            w.WriteAttributeString("w", "val", W, "single");
            w.WriteAttributeString("w", "sz", W, "4");
            w.WriteAttributeString("w", "space", W, "0");
            w.WriteAttributeString("w", "color", W, "auto");
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteStartElement("w", "tblCellMar", W);
        foreach (var (side, width) in new[] { ("left", 108), ("right", 108) })
        {
            w.WriteStartElement("w", side, W);
            w.WriteAttributeString("w", "w", W, width.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("w", "type", W, "dxa");
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Document(XmlWriter w, DocxContent content, IReadOnlyList<DocxPicture> pictures)
    {
        w.WriteStartElement("w", "document", W);
        w.WriteAttributeString("xmlns", "r", null, R);
        w.WriteAttributeString("xmlns", "wp", null, WP);
        w.WriteAttributeString("xmlns", "a", null, A);
        w.WriteAttributeString("xmlns", "pic", null, Pic);
        w.WriteStartElement("w", "body", W);
        var picture = 0;
        DocxBlock? previous = null;
        foreach (var block in content.Blocks)
        {
            if (previous is DocxTable or DocxColumns && block is DocxTable or DocxColumns)
            {
                // Two tables in a row would become one: a paragraph keeps them apart.
                w.WriteStartElement("w", "p", W);
                w.WriteEndElement();
            }

            switch (block)
            {
                case DocxParagraph paragraph:
                    Paragraph(w, paragraph);
                    break;
                case DocxTable table:
                    Table(w, table, content.Page);
                    break;
                case DocxColumns columns:
                    Columns(w, columns, content.Page);
                    break;
                case DocxPicture image:
                    Picture(w, image, ++picture, content.Page);
                    break;
                case DocxPageBreak:
                    w.WriteStartElement("w", "p", W);
                    w.WriteStartElement("w", "r", W);
                    w.WriteStartElement("w", "br", W);
                    w.WriteAttributeString("w", "type", W, "page");
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                    break;
            }

            previous = block;
        }

        if (previous is DocxTable or DocxColumns or null)
        {
            // A table is followed by a paragraph (Word wants one before the section's end); an empty body has one.
            w.WriteStartElement("w", "p", W);
            w.WriteEndElement();
        }

        Section(w, content.Page);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Paragraph(XmlWriter w, DocxParagraph paragraph)
    {
        w.WriteStartElement("w", "p", W);
        w.WriteStartElement("w", "pPr", W);
        if (paragraph.HeadingLevel is >= 1 and <= 3)
        {
            Value(w, "pStyle", $"Heading{paragraph.HeadingLevel}");
        }

        if (paragraph.SpaceBefore > 0)
        {
            w.WriteStartElement("w", "spacing", W);
            w.WriteAttributeString("w", "before", W, Twip(paragraph.SpaceBefore));
            w.WriteEndElement();
        }

        if (paragraph.LeftIndent > 0 || paragraph.FirstLineIndent != 0)
        {
            w.WriteStartElement("w", "ind", W);
            if (paragraph.LeftIndent > 0)
            {
                w.WriteAttributeString("w", "left", W, Twip(paragraph.LeftIndent));
            }

            if (paragraph.FirstLineIndent > 0)
            {
                w.WriteAttributeString("w", "firstLine", W, Twip(paragraph.FirstLineIndent));
            }
            else if (paragraph.FirstLineIndent < 0)
            {
                w.WriteAttributeString("w", "hanging", W, Twip(-paragraph.FirstLineIndent));
            }

            w.WriteEndElement();
        }

        if (paragraph.Alignment != DocxAlignment.Left)
        {
            Value(w, "jc", Justification(paragraph.Alignment));
        }

        w.WriteEndElement();
        foreach (var run in paragraph.Runs)
        {
            Run(w, run);
        }

        w.WriteEndElement();
    }

    private static void Run(XmlWriter w, DocxRun run)
    {
        var text = Clean(run.Text);
        if (text.Length == 0)
        {
            return;
        }

        w.WriteStartElement("w", "r", W);
        w.WriteStartElement("w", "rPr", W);
        if (!string.IsNullOrWhiteSpace(run.Font))
        {
            Fonts(w, run.Font);
        }

        if (run.Bold)
        {
            Element(w, "b");
            Element(w, "bCs");
        }

        if (run.Italic)
        {
            Element(w, "i");
            Element(w, "iCs");
        }

        if (run.Color is { Length: 6 } color && color.All(Uri.IsHexDigit))
        {
            Value(w, "color", color.ToUpperInvariant());
        }

        Value(w, "sz", HalfPoints(run.Size));
        Value(w, "szCs", HalfPoints(run.Size));
        w.WriteEndElement();

        // A tab or line break inside the text is its own element.
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\t' or '\n' or '\r'))
            {
                continue;
            }

            if (i > start)
            {
                w.WriteStartElement("w", "t", W);
                w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(text[start..i]);
                w.WriteEndElement();
            }

            if (i < text.Length)
            {
                Element(w, text[i] == '\t' ? "tab" : "br");
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }

            start = i + 1;
        }

        w.WriteEndElement();
    }

    private static void Table(XmlWriter w, DocxTable table, DocxPageSetup page)
    {
        var columns = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Count));
        var shares = Enumerable.Range(0, columns).Select(c => table.ColumnWidths is { } given && c < given.Count && given[c] > 0 ? given[c] : 1).ToList();
        var widths = shares.Select(s => (int)Math.Floor(page.ContentWidth * Twips * s / shares.Sum())).ToList();
        w.WriteStartElement("w", "tbl", W);
        w.WriteStartElement("w", "tblPr", W);
        Value(w, "tblStyle", "TableGrid");
        w.WriteStartElement("w", "tblW", W);
        w.WriteAttributeString("w", "w", W, widths.Sum().ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("w", "type", W, "dxa");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("w", "tblGrid", W);
        for (var c = 0; c < columns; c++)
        {
            w.WriteStartElement("w", "gridCol", W);
            w.WriteAttributeString("w", "w", W, widths[c].ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        w.WriteEndElement();
        foreach (var row in table.Rows.Count == 0 ? [[]] : table.Rows)
        {
            w.WriteStartElement("w", "tr", W);
            for (var c = 0; c < columns; c++)
            {
                w.WriteStartElement("w", "tc", W);
                w.WriteStartElement("w", "tcPr", W);
                w.WriteStartElement("w", "tcW", W);
                w.WriteAttributeString("w", "w", W, widths[c].ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("w", "type", W, "dxa");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("w", "p", W);
                if (c < row.Count && IsNumber(row[c]))
                {
                    w.WriteStartElement("w", "pPr", W);
                    Value(w, "jc", "right");
                    w.WriteEndElement();
                }

                if (c < row.Count && !string.IsNullOrEmpty(row[c]))
                {
                    Run(w, new DocxRun(row[c], table.Font, table.Size));
                }

                w.WriteEndElement();
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void Columns(XmlWriter w, DocxColumns columns, DocxPageSetup page)
    {
        var total = columns.Columns.Sum(c => Math.Max(1, c.Width));
        var widths = columns.Columns.Select(c => (int)Math.Floor(page.ContentWidth * Twips * Math.Max(1, c.Width) / total)).ToList();
        w.WriteStartElement("w", "tbl", W);
        w.WriteStartElement("w", "tblPr", W);
        w.WriteStartElement("w", "tblW", W);
        w.WriteAttributeString("w", "w", W, widths.Sum().ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("w", "type", W, "dxa");
        w.WriteEndElement();
        w.WriteStartElement("w", "tblBorders", W);
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            w.WriteStartElement("w", side, W);
            w.WriteAttributeString("w", "val", W, "nil");
            w.WriteEndElement();
        }

        w.WriteEndElement();
        Value(w, "tblLayout", "fixed");
        w.WriteStartElement("w", "tblCellMar", W);
        foreach (var side in new[] { "left", "right" })
        {
            w.WriteStartElement("w", side, W);
            w.WriteAttributeString("w", "w", W, "0");
            w.WriteAttributeString("w", "type", W, "dxa");
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("w", "tblGrid", W);
        foreach (var width in widths)
        {
            w.WriteStartElement("w", "gridCol", W);
            w.WriteAttributeString("w", "w", W, width.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteStartElement("w", "tr", W);
        for (var c = 0; c < columns.Columns.Count; c++)
        {
            w.WriteStartElement("w", "tc", W);
            w.WriteStartElement("w", "tcPr", W);
            w.WriteStartElement("w", "tcW", W);
            w.WriteAttributeString("w", "w", W, widths[c].ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("w", "type", W, "dxa");
            w.WriteEndElement();
            w.WriteEndElement();
            if (columns.Columns[c].Paragraphs.Count == 0)
            {
                w.WriteStartElement("w", "p", W);
                w.WriteEndElement();
            }

            foreach (var paragraph in columns.Columns[c].Paragraphs)
            {
                Paragraph(w, paragraph);
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Picture(XmlWriter w, DocxPicture picture, int number, DocxPageSetup page)
    {
        // Wider than the margins allow: smaller, the same shape.
        var scale = picture.Width > page.ContentWidth ? page.ContentWidth / picture.Width : 1;
        var width = (long)Math.Round(Math.Max(1, picture.Width * scale) * Emu);
        var height = (long)Math.Round(Math.Max(1, picture.Height * scale) * Emu);
        var id = number.ToString(CultureInfo.InvariantCulture);
        w.WriteStartElement("w", "p", W);
        if (picture.Alignment != DocxAlignment.Left)
        {
            w.WriteStartElement("w", "pPr", W);
            Value(w, "jc", Justification(picture.Alignment));
            w.WriteEndElement();
        }

        w.WriteStartElement("w", "r", W);
        w.WriteStartElement("w", "drawing", W);
        w.WriteStartElement("wp", "inline", WP);
        foreach (var distance in new[] { "distT", "distB", "distL", "distR" })
        {
            w.WriteAttributeString(distance, "0");
        }

        Extent(w, WP, "extent", width, height);
        w.WriteStartElement("wp", "docPr", WP);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("name", "Picture " + id);
        w.WriteEndElement();
        w.WriteStartElement("a", "graphic", A);
        w.WriteStartElement("a", "graphicData", A);
        w.WriteAttributeString("uri", Pic);
        w.WriteStartElement("pic", "pic", Pic);
        w.WriteStartElement("pic", "nvPicPr", Pic);
        w.WriteStartElement("pic", "cNvPr", Pic);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("name", $"image{id}{(picture.IsJpeg ? ".jpeg" : ".png")}");
        w.WriteEndElement();
        w.WriteStartElement("pic", "cNvPicPr", Pic);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("pic", "blipFill", Pic);
        w.WriteStartElement("a", "blip", A);
        w.WriteAttributeString("r", "embed", R, "rIdImage" + id);
        w.WriteEndElement();
        w.WriteStartElement("a", "stretch", A);
        w.WriteStartElement("a", "fillRect", A);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("pic", "spPr", Pic);
        w.WriteStartElement("a", "xfrm", A);
        w.WriteStartElement("a", "off", A);
        w.WriteAttributeString("x", "0");
        w.WriteAttributeString("y", "0");
        w.WriteEndElement();
        Extent(w, A, "ext", width, height);
        w.WriteEndElement();
        w.WriteStartElement("a", "prstGeom", A);
        w.WriteAttributeString("prst", "rect");
        w.WriteStartElement("a", "avLst", A);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        static void Extent(XmlWriter w, string ns, string name, long width, long height)
        {
            w.WriteStartElement(ns == WP ? "wp" : "a", name, ns);
            w.WriteAttributeString("cx", width.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("cy", height.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
    }

    private static void Section(XmlWriter w, DocxPageSetup page)
    {
        w.WriteStartElement("w", "sectPr", W);
        w.WriteStartElement("w", "pgSz", W);
        w.WriteAttributeString("w", "w", W, Twip(page.Width));
        w.WriteAttributeString("w", "h", W, Twip(page.Height));
        if (page.Width > page.Height)
        {
            w.WriteAttributeString("w", "orient", W, "landscape");
        }

        w.WriteEndElement();
        w.WriteStartElement("w", "pgMar", W);
        w.WriteAttributeString("w", "top", W, Twip(page.Top));
        w.WriteAttributeString("w", "right", W, Twip(page.Right));
        w.WriteAttributeString("w", "bottom", W, Twip(page.Bottom));
        w.WriteAttributeString("w", "left", W, Twip(page.Left));
        w.WriteAttributeString("w", "header", W, "709");
        w.WriteAttributeString("w", "footer", W, "709");
        w.WriteAttributeString("w", "gutter", W, "0");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Fonts(XmlWriter w, string font)
    {
        w.WriteStartElement("w", "rFonts", W);
        foreach (var slot in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
        {
            w.WriteAttributeString("w", slot, W, font);
        }

        w.WriteEndElement();
    }

    private static void Value(XmlWriter w, string name, string value)
    {
        w.WriteStartElement("w", name, W);
        w.WriteAttributeString("w", "val", W, value);
        w.WriteEndElement();
    }

    private static void Element(XmlWriter w, string name)
    {
        w.WriteStartElement("w", name, W);
        w.WriteEndElement();
    }

    /// <summary>"1.250.000", "12,5", "30 %": an amount, set to the right of its cell ("1" — a row number — is not).</summary>
    internal static bool IsNumber(string text)
    {
        var core = text.Trim().TrimEnd('%', ' ');
        return core.Length >= 2 && char.IsAsciiDigit(core[^1])
            && core.All(c => char.IsAsciiDigit(c) || c is '.' or ',' or ' ' or '-' or '+')
            && core.Count(char.IsAsciiDigit) >= 2;
    }

    private static string Justification(DocxAlignment alignment) => alignment switch
    {
        DocxAlignment.Center => "center",
        DocxAlignment.Right => "right",
        DocxAlignment.Justify => "both",
        _ => "left",
    };

    private static string Twip(double points) => ((int)Math.Round(points * Twips)).ToString(CultureInfo.InvariantCulture);

    private static string HalfPoints(double points) => Math.Max(2, (int)Math.Round(points * 2)).ToString(CultureInfo.InvariantCulture);
}
