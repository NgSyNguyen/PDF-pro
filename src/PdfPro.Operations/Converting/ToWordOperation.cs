using System.Globalization;
using System.Text;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Core.Word;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Optimizing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Converting;

/// <param name="Pages">Page range ("1-3, 8", "odd", "even"); null or empty means all pages.</param>
/// <param name="Tables">Tables drawn with rules become Word tables.</param>
/// <param name="Pictures">The pictures of the pages come along.</param>
/// <param name="Overwrite">Replace a file of the same name (a run on files; the app asks where to save).</param>
public sealed record ToWordParams(string? Pages = null, bool Tables = true, bool Pictures = true, bool Overwrite = false);

/// <summary>What was written.</summary>
/// <param name="Output">The Word document.</param>
/// <param name="PageCount">Pages read.</param>
/// <param name="Paragraphs">Paragraphs written (headings included).</param>
/// <param name="Tables">Tables written.</param>
/// <param name="Pictures">Pictures written.</param>
/// <param name="PagesWithoutText">Pages (0-based) with pictures but no text: scans, which OCR can make editable.</param>
public sealed record ToWordResult(string Output, int PageCount, int Paragraphs, int Tables, int Pictures, IReadOnlyList<int> PagesWithoutText);

/// <summary>
/// convert.toWord (<see cref="OperationKind.Export"/>, "PDF sang Word"): a Word document (.docx) whose text can be
/// edited — not pictures of the pages. PdfPig reads the letters with their fonts, sizes, weights and colours; the words
/// are grouped into blocks and ordered as they are read (as "Sang văn bản" does), each block becoming paragraphs —
/// a new one where a line is indented or the previous line stops short — lined up as on the page (centred, right,
/// justified, indented), with the space above them; short blocks set larger than the page's text become headings
/// (Word's "Heading 1/2": navigation pane, table of contents). Tables drawn with rules (Tabula's lattice) become Word
/// tables, their text taken out of the paragraphs; pictures come along as they are (JPEG byte for byte, others as
/// PNG) at their place among the paragraphs and their size on the page — except, on a page with text, a picture
/// behind most of the page (a scan under recognised text, a background). Each page of the PDF starts a page of the
/// document; the page size and margins are those of the first page. Pages with pictures but no text are reported:
/// OCR makes them editable. The file is written under a temporary name and moved into place at the end.
/// </summary>
public sealed class ToWordOperation : PdfOperation<ToWordParams>, IExportOperation
{
    public const string OperationId = "convert.toWord";

    public const string Stage = "Stage_ToWord";

    /// <summary>A block this many times larger than the page's text (and short) is a heading.</summary>
    private const double HeadingRatio = 1.25;

    private const double TitleRatio = 1.6;

    /// <summary>A picture behind this share of a page that has text is its background (or a scan under OCR text).</summary>
    private const double BackgroundShare = 0.6;

    Type IExportOperation.ResultType => typeof(ToWordResult);

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    async Task<object> IExportOperation.ExportAsync(ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await WriteAsync(call.Input, call.Password, call.Output, (ToWordParams)parameters, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the Word document of <paramref name="input"/> as <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="Security.DocumentPasswordException">The file is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    /// <exception cref="OperationValidationException">The parameters or the page range are not valid.</exception>
    public Task<ToWordResult> WriteAsync(string input, string? password, string output, ToWordParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(parameters);
        return OptimizedFiles.WriteAsync(
            output,
            (temporary, token) => Task.Run(
                () =>
                {
                    var (content, result) = Read(input, password, parameters, progress, token);
                    using (var stream = File.Create(temporary))
                    {
                        DocxWriter.Write(stream, content);
                    }

                    return result with { Output = output };
                },
                token),
            cancellationToken);
    }

    /// <summary>The family name of a PDF font ("ABCDEF+TimesNewRomanPS-BoldMT" → "Times New Roman").</summary>
    internal static string FamilyOf(string? pdfName)
    {
        if (string.IsNullOrWhiteSpace(pdfName))
        {
            return "Times New Roman";
        }

        var name = pdfName.Trim();
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
        {
            name = name[7..];
        }

        // The style part: after a hyphen or a comma ("Arial-BoldMT", "Arial,Bold"), or a known suffix.
        var cut = name.IndexOfAny(['-', ',']);
        if (cut > 0)
        {
            name = name[..cut];
        }

        foreach (var suffix in new[] { "PSMT", "PS", "MT" })
        {
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        foreach (var style in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular" })
        {
            if (name.Length > style.Length + 2 && name.EndsWith(style, StringComparison.Ordinal))
            {
                name = name[..^style.Length];
                break;
            }
        }

        if (name.StartsWith('.') || name.Contains(' ', StringComparison.Ordinal))
        {
            // ".VnTime" (TCVN3) and names that already have their spaces stay as they are.
            return name;
        }

        // "TimesNewRoman" → "Times New Roman"; "NotoSans" → "Noto Sans"; "SegoeUI" → "Segoe UI".
        var spaced = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
            {
                spaced.Append(' ');
            }

            spaced.Append(name[i]);
        }

        return spaced.ToString();
    }

    protected override OperationValidationResult ValidateCore(ToWordParams parameters, DocumentInfo document)
        => string.IsNullOrWhiteSpace(parameters.Pages) || PageRanges.TryParse(parameters.Pages, document.PageCount, out _, out _)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), $"The document has {document.PageCount} pages."));

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ToWordParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var inputs = context.InputPaths.Select(Path.GetFullPath).ToList();
        var names = FileNames.MakeUnique([.. inputs.Select(p => Path.GetFileNameWithoutExtension(p))], name => !parameters.Overwrite && File.Exists(Path.Combine(context.OutputDirectory, name + ".docx")));
        var files = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var output = Path.GetFullPath(Path.Combine(context.OutputDirectory, names[i] + ".docx"));
            files.Add((await WriteAsync(inputs[i], context.Passwords?.For(context.InputPaths[i]), output, parameters, progress, cancellationToken).ConfigureAwait(false)).Output);
        }

        return OperationResult.Success(files);
    }

    /// <summary>The document's pages as a Word document, and what it holds.</summary>
    private (DocxContent Content, ToWordResult Result) Read(string input, string? password, ToWordParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var document = ToTextOperation.Open(input, password);
        var pageCount = document.NumberOfPages;
        if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(parameters.Pages) ? "1-" + pageCount.ToString(CultureInfo.InvariantCulture) : parameters.Pages, pageCount, out var pages, out _) || pages.Count == 0)
        {
            throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Pages), $"The document has {pageCount} pages.")]);
        }

        var layouts = new List<PageLayout>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(i, pages.Count, Stage));
            layouts.Add(PageLayout.Read(document, ToTextOperation.ReadPage(document, pages[i]), pages[i], parameters));
        }

        // Lines at the top or the bottom of most pages ("Trang 2/3", the running title) are the PDF's page furniture:
        // Word numbers its own pages, so they are left out.
        var running = layouts.Count < 2 ? [] : layouts
            .SelectMany(l => l.Items.Select(item => item.Running).OfType<string>().Distinct())
            .GroupBy(s => s, StringComparer.Ordinal)
            .Where(g => g.Count() >= Math.Max(2, (layouts.Count + 1) / 2))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var blocks = new List<DocxBlock>();
        var withoutText = new List<int>();
        var fonts = new Dictionary<(string Font, double Size), int>();
        var (paragraphs, tables, pictures) = (0, 0, 0);
        for (var i = 0; i < layouts.Count; i++)
        {
            var layout = layouts[i];
            var kept = layout.Items.Where(item => item.Running is null || !running.Contains(item.Running)).Select(item => item.Block).ToList();
            if (layout.Paragraphs == 0 && layout.Pictures > 0)
            {
                withoutText.Add(pages[i]);
            }

            foreach (var (key, count) in layout.Fonts)
            {
                fonts[key] = fonts.GetValueOrDefault(key) + count;
            }

            if (i > 0)
            {
                blocks.Add(new DocxPageBreak());
            }

            blocks.AddRange(kept);
            paragraphs += kept.OfType<DocxParagraph>().Count() + kept.OfType<DocxColumns>().Sum(c => c.Columns.Sum(column => column.Paragraphs.Count));
            tables += layout.Tables;
            pictures += layout.Pictures;
        }

        progress?.Report(new OperationProgress(pages.Count, pages.Count, Stage));
        var body = fonts.Count == 0 ? ("Times New Roman", 12d) : fonts.MaxBy(f => f.Value).Key;
        var setup = layouts.Select(l => l.Setup).FirstOrDefault(s => s is not null) ?? DocxPageSetup.A4;
        var content = new DocxContent(blocks, setup, body.Item1, body.Item2, Path.GetFileNameWithoutExtension(input));
        return (content, new ToWordResult(string.Empty, pages.Count, paragraphs, tables, pictures, withoutText));
    }

    /// <summary>One page laid out as Word blocks.</summary>
    private sealed class PageLayout
    {
        /// <summary>The share of the page's height, at its top and bottom, where running lines are looked for.</summary>
        private const double Band = 0.08;

        private readonly List<(double Top, DocxBlock Block)> _others = [];
        private double _left;
        private double _right;
        private double _height;
        private double _body;

        /// <summary>The page's blocks in order; <c>Running</c> is the shape of a paragraph in the top or bottom band.</summary>
        public List<(DocxBlock Block, string? Running)> Items { get; } = [];

        public Dictionary<(string Font, double Size), int> Fonts { get; } = [];

        public DocxPageSetup? Setup { get; private set; }

        public int Paragraphs { get; private set; }

        public int Tables { get; private set; }

        public int Pictures { get; private set; }

        public static PageLayout Read(PigDocument document, Page page, int index, ToWordParams parameters)
        {
            var layout = new PageLayout { _height = page.Height };
            var ruled = parameters.Tables ? TableExtractor.RuledTables(document, index) : [];
            var letters = page.Letters
                .Where(l => !string.IsNullOrWhiteSpace(l.Value) && !ruled.Any(t => Inside(t.Box, l.BoundingBox.Centroid.X, l.BoundingBox.Centroid.Y)))
                .ToList();
            var blocks = ToTextOperation.Blocks(letters);
            var lines = blocks.SelectMany(b => b.TextLines).ToList();
            if (lines.Count > 0)
            {
                layout._left = lines.Min(l => l.BoundingBox.Left);
                layout._right = lines.Max(l => l.BoundingBox.Right);
                var top = lines.Max(l => l.BoundingBox.Top);
                var bottom = lines.Min(l => l.BoundingBox.Bottom);
                layout.Setup = new DocxPageSetup(
                    page.Width,
                    page.Height,
                    Math.Clamp(page.Height - top, 28, 85),
                    Math.Clamp(page.Width - layout._right, 28, 113),
                    Math.Clamp(bottom, 28, 85),
                    Math.Clamp(layout._left, 28, 113));
                var sizes = letters.Select(l => Math.Round(l.PointSize * 2) / 2).ToList();
                layout._body = sizes.GroupBy(s => s).MaxBy(g => g.Count())!.Key;
            }

            foreach (var table in ruled)
            {
                var inside = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value) && Inside(table.Box, l.BoundingBox.Centroid.X, l.BoundingBox.Centroid.Y)).Select(Style).ToList();
                var font = inside.Count > 0 ? inside.GroupBy(s => s.Font).MaxBy(g => g.Count())!.Key : null;
                var size = inside.Count > 0 ? inside.GroupBy(s => s.Size).MaxBy(g => g.Count())!.Key : layout._body > 0 ? layout._body : 11;
                var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count);
                var widths = Enumerable.Range(0, columns).Select(c => (double)Math.Max(3, table.Rows.Max(r => c < r.Count ? r[c].Length : 0))).ToList();
                layout._others.Add((table.Box.Top, new DocxTable(table.Rows, font, size, widths)));
                layout.Tables++;
            }

            if (parameters.Pictures)
            {
                layout.AddPictures(page, hasText: lines.Count > 0);
            }

            // Blocks side by side (the two halves of an administrative heading, columns) go together, at the place of
            // the first of them; tables and pictures before the first block (in reading order) below them.
            var groups = SideBySide(blocks);
            var others = layout._others.OrderByDescending(o => o.Top).ToList();
            var previousBottom = double.NaN;
            var done = new HashSet<TextBlock>();
            foreach (var block in blocks)
            {
                if (!done.Add(block))
                {
                    continue;
                }

                while (others.Count > 0 && others[0].Top > block.BoundingBox.Top + 2)
                {
                    layout.Items.Add((others[0].Block, null));
                    others.RemoveAt(0);
                    previousBottom = double.NaN;
                }

                if (groups.TryGetValue(block, out var group))
                {
                    done.UnionWith(group.SelectMany(c => c));
                    previousBottom = layout.AddColumns(group, previousBottom);
                }
                else
                {
                    previousBottom = layout.AddBlock(block, previousBottom);
                }
            }

            layout.Items.AddRange(others.Select(o => (o.Block, (string?)null)));
            return layout;
        }

        /// <summary>
        /// Blocks that share a band of the page without sharing its width: rows of blocks whose heights overlap, split
        /// into columns where nothing crosses between them. Each group (two columns or more), by its first block in
        /// reading order; its columns left to right, their blocks top to bottom.
        /// </summary>
        private static Dictionary<TextBlock, List<List<TextBlock>>> SideBySide(IReadOnlyList<TextBlock> blocks)
        {
            var groups = new Dictionary<TextBlock, List<List<TextBlock>>>();
            var rows = new List<List<TextBlock>>();
            foreach (var block in blocks.OrderByDescending(b => b.BoundingBox.Top))
            {
                var row = rows.LastOrDefault();
                if (row is not null && row.Min(b => b.BoundingBox.Bottom) < block.BoundingBox.Top - 1)
                {
                    row.Add(block);
                }
                else
                {
                    rows.Add([block]);
                }
            }

            foreach (var row in rows.Where(r => r.Count > 1))
            {
                var columns = new List<List<TextBlock>>();
                foreach (var block in row.OrderBy(b => b.BoundingBox.Left))
                {
                    var column = columns.LastOrDefault();
                    if (column is not null && column.Max(b => b.BoundingBox.Right) > block.BoundingBox.Left - 2)
                    {
                        column.Add(block);
                    }
                    else
                    {
                        columns.Add([block]);
                    }
                }

                if (columns.Count < 2)
                {
                    continue;
                }

                foreach (var column in columns)
                {
                    column.Sort((a, b) => b.BoundingBox.Top.CompareTo(a.BoundingBox.Top));
                }

                var first = blocks.First(row.Contains);
                groups[first] = columns;
            }

            return groups;
        }

        private static bool Inside(PdfPro.Core.Geometry.PdfRect box, double x, double y)
            => x >= box.Left - 1 && x <= box.Right + 1 && y >= box.Bottom - 1 && y <= box.Top + 1;

        /// <summary>Font family, size (to the half point), bold, italic and colour of a letter.</summary>
        private static (string Font, double Size, bool Bold, bool Italic, string? Color) Style(Letter letter)
        {
            var name = letter.FontName ?? letter.FontDetails?.Name;
            var bold = letter.FontDetails?.IsBold == true || (name?.Contains("Bold", StringComparison.OrdinalIgnoreCase) ?? false) || (name?.Contains("Black", StringComparison.OrdinalIgnoreCase) ?? false);
            var italic = letter.FontDetails?.IsItalic == true || (name?.Contains("Italic", StringComparison.OrdinalIgnoreCase) ?? false) || (name?.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ?? false);
            string? color = null;
            try
            {
                var (r, g, b) = letter.Color?.ToRGBValues() ?? (0, 0, 0);
                if (r > 0.15 || g > 0.15 || b > 0.15)
                {
                    color = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A colour PdfPig cannot turn into RGB: black.
            }

            return (FamilyOf(name), Math.Round(letter.PointSize * 2) / 2, bold, italic, color);
        }

        /// <summary>The shape of a running line: its text with every digit as '#' ("Trang #/#").</summary>
        private static string Shape(IEnumerable<TextLine> lines)
            => string.Concat(string.Join(' ', lines.Select(l => l.Text)).Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c)).Select(c => char.IsDigit(c) ? '#' : c));

        /// <summary>The paragraphs of a block, between the page's text edges; the bottom of its last line.</summary>
        private double AddBlock(TextBlock block, double previousBottom)
        {
            var running = block.BoundingBox.Bottom > _height * (1 - Band) || block.BoundingBox.Top < _height * Band
                ? Shape(block.TextLines)
                : null;
            foreach (var paragraph in ParagraphsOf(block, _left, _right, ref previousBottom))
            {
                Items.Add((paragraph, running));
            }

            return previousBottom;
        }

        /// <summary>Blocks side by side, as columns of a table without borders; the bottom of the lowest.</summary>
        private double AddColumns(List<List<TextBlock>> columns, double previousBottom)
        {
            var result = new List<DocxColumn>();
            var bottom = double.MaxValue;
            var spans = new List<double>();
            var needs = new List<double>();
            for (var c = 0; c < columns.Count; c++)
            {
                // A column runs from its own left edge (the page's text edge for the first) to the next one's.
                var left = c == 0 ? Math.Min(_left, columns[c].Min(b => b.BoundingBox.Left)) : columns[c].Min(b => b.BoundingBox.Left);
                var right = c == columns.Count - 1 ? Math.Max(_right, columns[c].Max(b => b.BoundingBox.Right)) : columns[c + 1].Min(b => b.BoundingBox.Left);
                var paragraphs = new List<DocxParagraph>();
                var above = double.NaN;
                foreach (var block in columns[c])
                {
                    paragraphs.AddRange(ParagraphsOf(block, left, right, ref above));
                }

                result.Add(new DocxColumn(right - left, paragraphs));
                spans.Add(right - left);
                needs.Add(1.1 * columns[c].SelectMany(b => b.TextLines).Max(l => l.BoundingBox.Width));
                bottom = Math.Min(bottom, columns[c].Min(b => b.BoundingBox.Bottom));
            }

            // Each column at least as wide as its longest line and a tenth (the word processor's fonts may be wider):
            // what the others have to spare is shared out by how much they spare.
            var total = spans.Sum();
            var needed = needs.Select(n => Math.Min(n, total)).ToList();
            var spare = spans.Select((s, i) => Math.Max(0, s - needed[i])).ToList();
            var extra = total - needed.Sum();
            for (var c = 0; c < result.Count; c++)
            {
                var width = extra >= 0
                    ? needed[c] + (spare.Sum() > 0 ? extra * spare[c] / spare.Sum() : extra / result.Count)
                    : needed[c] * total / needed.Sum();
                result[c] = result[c] with { Width = Math.Max(1, width) };
            }

            var top = columns.Max(column => column.Max(b => b.BoundingBox.Top));
            var space = double.IsNaN(previousBottom) || previousBottom <= top ? 0 : Math.Clamp(Math.Round(previousBottom - top - (0.3 * _body)), 0, 48);
            if (space > 0 && result[0].Paragraphs.Count > 0)
            {
                result[0] = result[0] with { Paragraphs = [result[0].Paragraphs[0] with { SpaceBefore = space }, .. result[0].Paragraphs.Skip(1)] };
            }

            var running = bottom > _height * (1 - Band) || top < _height * Band
                ? Shape(columns.SelectMany(column => column).OrderByDescending(b => b.BoundingBox.Top).ThenBy(b => b.BoundingBox.Left).SelectMany(b => b.TextLines))
                : null;
            Items.Add((new DocxColumns(result), running));
            return bottom;
        }

        /// <summary>
        /// The paragraphs of a block lined up between <paramref name="left"/> and <paramref name="right"/>: a new one
        /// where a line is indented or the line before stops short; short and large, a heading.
        /// </summary>
        private List<DocxParagraph> ParagraphsOf(TextBlock block, double left, double right, ref double previousBottom)
        {
            var result = new List<DocxParagraph>();
            var lines = block.TextLines.Where(l => l.Words.Count > 0).ToList();
            if (lines.Count == 0)
            {
                return result;
            }

            var sizes = lines.Select(l => Median([.. l.Words.SelectMany(w => w.Letters).Select(x => x.PointSize)])).ToList();
            var blockLeft = lines.Min(l => l.BoundingBox.Left);
            var blockRight = lines.Max(l => l.BoundingBox.Right);
            var start = 0;
            for (var i = 1; i <= lines.Count; i++)
            {
                var resized = i < lines.Count && Math.Max(sizes[i], sizes[i - 1]) > 1.15 * Math.Min(sizes[i], sizes[i - 1]);
                if (i < lines.Count && !resized && !StartsParagraph(lines[i - 1], lines[i], blockLeft, blockRight, sizes[i - 1]))
                {
                    continue;
                }

                var paragraph = lines.GetRange(start, i - start);
                var size = Median(sizes.GetRange(start, i - start));
                var heading = _body > 0 && paragraph.Count <= 3 ? (size >= _body * TitleRatio ? 1 : size >= _body * HeadingRatio ? 2 : 0) : 0;
                var top = paragraph[0].BoundingBox.Top;
                var space = double.IsNaN(previousBottom) || previousBottom <= top ? 0 : Math.Clamp(Math.Round(previousBottom - top - (0.3 * size)), 0, 48);
                result.Add(Paragraph(paragraph, heading, space, left, right));
                Paragraphs++;
                previousBottom = paragraph[^1].BoundingBox.Bottom;
                start = i;
            }

            return result;
        }

        /// <summary>A line starts a paragraph when it is indented, or when the line before stops well short of the block's right edge.</summary>
        private static bool StartsParagraph(TextLine previous, TextLine line, double blockLeft, double blockRight, double size)
        {
            var width = Math.Max(1, blockRight - blockLeft);
            var indented = line.BoundingBox.Left - blockLeft > 0.8 * size && previous.BoundingBox.Left - blockLeft < 0.5 * size;
            var shortBefore = blockRight - previous.BoundingBox.Right > 0.2 * width && width > 20 * size;
            return indented || shortBefore;
        }

        private DocxParagraph Paragraph(List<TextLine> lines, int heading, double spaceBefore, double areaLeft, double areaRight)
        {
            var runs = new List<DocxRun>();
            var text = new StringBuilder();
            (string Font, double Size, bool Bold, bool Italic, string? Color)? current = null;
            void Flush()
            {
                if (current is { } style && text.Length > 0)
                {
                    runs.Add(new DocxRun(text.ToString(), style.Font, style.Size, style.Bold || heading > 0, style.Italic, style.Color));
                }

                text.Clear();
            }

            var first = true;
            for (var l = 0; l < lines.Count; l++)
            {
                foreach (var word in lines[l].Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)))
                {
                    var letter = word.Letters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Value)) ?? word.Letters[0];
                    var style = Style(letter);
                    Fonts[(style.Font, style.Size)] = Fonts.GetValueOrDefault((style.Font, style.Size)) + word.Letters.Count;

                    // A word broken by a hyphen at the end of a line is joined; otherwise a space, which belongs to
                    // the run before the word.
                    if (!first)
                    {
                        var hyphenated = l > 0 && word == lines[l].Words[0] && text.Length >= 2 && text[^1] == '-' && char.IsLetter(text[^2]) && char.IsLower(word.Text[0]);
                        if (hyphenated)
                        {
                            text.Length--;
                        }
                        else
                        {
                            text.Append(' ');
                        }
                    }

                    if (current != style)
                    {
                        Flush();
                        current = style;
                    }

                    text.Append(word.Text);
                    first = false;
                }
            }

            Flush();

            var alignment = AlignmentOf(lines, areaLeft, areaRight);
            var left = lines.Count > 1 ? lines.Skip(1).Min(l => l.BoundingBox.Left) : lines[0].BoundingBox.Left;
            var indent = alignment is DocxAlignment.Left or DocxAlignment.Justify ? Math.Max(0, Math.Round(left - areaLeft)) : 0;
            var firstLine = alignment is DocxAlignment.Left or DocxAlignment.Justify && lines.Count > 1 ? Math.Round(lines[0].BoundingBox.Left - left) : 0;
            return new DocxParagraph(runs, alignment, heading, spaceBefore, indent < 3 ? 0 : indent, Math.Abs(firstLine) < 3 ? 0 : firstLine);
        }

        /// <summary>Centred, right, justified or left, from where the lines sit between the text edges of their area.</summary>
        private static DocxAlignment AlignmentOf(List<TextLine> lines, double areaLeft, double areaRight)
        {
            var width = Math.Max(1, areaRight - areaLeft);
            var center = (areaLeft + areaRight) / 2;
            bool AtRight(TextLine l) => areaRight - l.BoundingBox.Right < 0.02 * width;
            bool AtLeft(TextLine l) => l.BoundingBox.Left - areaLeft < 0.02 * width;
            if (lines.All(l => Math.Abs(((l.BoundingBox.Left + l.BoundingBox.Right) / 2) - center) < 0.04 * width) && lines.Any(l => !AtLeft(l)))
            {
                return DocxAlignment.Center;
            }

            if (lines.All(AtRight) && lines.Any(l => l.BoundingBox.Left - areaLeft > 0.3 * width))
            {
                return DocxAlignment.Right;
            }

            return lines.Count >= 2 && lines.Take(lines.Count - 1).All(AtRight) ? DocxAlignment.Justify : DocxAlignment.Left;
        }

        private void AddPictures(Page page, bool hasText)
        {
            var area = Math.Max(1, page.Width * page.Height);
            var center = page.Width / 2;
            foreach (var image in page.GetImages())
            {
                var bounds = image.BoundingBox;
                if (image.IsImageMask || bounds.Width < 8 || bounds.Height < 8 || (hasText && bounds.Width * bounds.Height > BackgroundShare * area))
                {
                    continue;
                }

                byte[] data;
                bool jpeg;
                if (image.TryGetPng(out var png) && png is { Length: > 0 })
                {
                    (data, jpeg) = (png, false);
                }
                else if (image.RawMemory.Span is [0xFF, 0xD8, 0xFF, ..])
                {
                    (data, jpeg) = (image.RawMemory.ToArray(), true);
                }
                else
                {
                    continue;
                }

                var middle = (bounds.Left + bounds.Right) / 2;
                var alignment = Math.Abs(middle - center) < 0.1 * page.Width ? DocxAlignment.Center : middle < center ? DocxAlignment.Left : DocxAlignment.Right;
                _others.Add((bounds.Top, new DocxPicture(data, jpeg, bounds.Width, bounds.Height, alignment)));
                Pictures++;
            }
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            values.Sort();
            return values[values.Count / 2];
        }
    }
}
