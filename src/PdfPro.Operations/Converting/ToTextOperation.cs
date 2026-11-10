using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Security;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PigOptions = UglyToad.PdfPig.ParsingOptions;

namespace PdfPro.Operations.Converting;

/// <summary>The kind of text file written.</summary>
public enum TextFileFormat
{
    /// <summary>Plain text: the lines of each block as on the page, a blank line between blocks.</summary>
    Text,

    /// <summary>Markdown: each block one paragraph, large short blocks as headings.</summary>
    Markdown,
}

/// <summary>How pages are kept apart.</summary>
public enum TextPageSplit
{
    /// <summary>One file; pages follow each other like blocks.</summary>
    None,

    /// <summary>One file; a form feed (plain text) or a horizontal rule (Markdown) between pages.</summary>
    PageBreaks,

    /// <summary>One file per page.</summary>
    FilePerPage,
}

/// <param name="Format">Plain text or Markdown.</param>
/// <param name="Split">How pages are kept apart.</param>
/// <param name="Pages">Page range ("1-3, 8", "odd", "even"); null or empty means all pages.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2)").</param>
public sealed record ToTextParams(
    TextFileFormat Format = TextFileFormat.Text,
    TextPageSplit Split = TextPageSplit.PageBreaks,
    string? Pages = null,
    bool Overwrite = false);

/// <summary>What was written.</summary>
/// <param name="Files">The files, in page order.</param>
/// <param name="PageCount">Pages read.</param>
/// <param name="Characters">Characters written (line breaks included).</param>
/// <param name="PagesWithoutText">Pages (0-based) with pictures but no text: scans, which OCR can make readable.</param>
public sealed record ToTextResult(IReadOnlyList<string> Files, int PageCount, long Characters, IReadOnlyList<int> PagesWithoutText);

/// <summary>
/// convert.toText (<see cref="OperationKind.Export"/>, "PDF sang văn bản"): the text of pages in reading order, as
/// plain text or Markdown in UTF-8 (NFC). PdfPig groups the letters into words, the words into lines and blocks
/// (Docstrum), and orders the blocks the way they are read (columns one after the other, the two halves of a
/// Vietnamese administrative heading each in one piece). Pages with pictures but no text are reported: they need OCR.
/// Files are written under temporary names and renamed when everything is done: a failure or a cancel leaves the output
/// folder as it was.
/// </summary>
public sealed class ToTextOperation : PdfOperation<ToTextParams>
{
    public const string OperationId = "convert.toText";

    public const string Stage = "Stage_ToText";

    /// <summary>Line breaks of the files written (the app is for Windows).</summary>
    private const string NewLine = "\r\n";

    /// <summary>A block this many times larger than the page's text (and short) is a heading in Markdown.</summary>
    private const double HeadingRatio = 1.25;

    private const double TitleRatio = 1.6;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>The file extension of a format (".txt", ".md").</summary>
    public static string Extension(TextFileFormat format) => format == TextFileFormat.Markdown ? ".md" : ".txt";

    /// <summary>
    /// Writes the text of <paramref name="input"/> into <paramref name="outputFolder"/>: "name.txt", or "name-001.txt"…
    /// (numbered by page of the document) with one file per page.
    /// </summary>
    /// <exception cref="DocumentPasswordException">The file is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    /// <exception cref="OperationValidationException">The parameters or the page range are not valid.</exception>
    public Task<ToTextResult> WriteAsync(
        string input,
        string? password,
        string outputFolder,
        string baseName,
        ToTextParams parameters,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        return Task.Run(() => Write(input, password, outputFolder, baseName, parameters, progress, cancellationToken), cancellationToken);
    }

    /// <summary>The text of one page in reading order, as <paramref name="format"/> (no trailing line break).</summary>
    internal static string PageText(Page page, TextFileFormat format)
    {
        var blocks = Blocks(page);
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        var body = format == TextFileFormat.Markdown ? BodySize(page) : 0;
        foreach (var block in blocks)
        {
            if (text.Length > 0)
            {
                text.Append(NewLine).Append(NewLine);
            }

            if (format == TextFileFormat.Markdown)
            {
                AppendMarkdown(text, block, body);
            }
            else
            {
                text.AppendJoin(NewLine, block.TextLines.Select(l => Clean(l.Text)));
            }
        }

        return text.ToString().Normalize(NormalizationForm.FormC);
    }

    protected override OperationValidationResult ValidateCore(ToTextParams parameters, DocumentInfo document)
        => string.IsNullOrWhiteSpace(parameters.Pages) || PageRanges.TryParse(parameters.Pages, document.PageCount, out _, out _)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), $"The document has {document.PageCount} pages."));

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ToTextParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var input in context.InputPaths)
        {
            var result = await WriteAsync(input, context.Passwords?.For(input), context.OutputDirectory, Path.GetFileNameWithoutExtension(input), parameters, progress, cancellationToken).ConfigureAwait(false);
            files.AddRange(result.Files);
        }

        return OperationResult.Success(files);
    }

    private ToTextResult Write(string input, string? password, string outputFolder, string baseName, ToTextParams parameters, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var document = Open(input, password);
        var pageCount = document.NumberOfPages;
        if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(parameters.Pages) ? "1-" + pageCount.ToString(CultureInfo.InvariantCulture) : parameters.Pages, pageCount, out var pages, out _) || pages.Count == 0)
        {
            throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Pages), $"The document has {pageCount} pages.")]);
        }

        // Every page is read before anything is written: a damaged page fails the whole run, leaving nothing behind.
        var texts = new List<string>(pages.Count);
        var withoutText = new List<int>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(i, pages.Count, Stage));
            var page = ReadPage(document, pages[i]);
            var text = PageText(page, parameters.Format);
            if (text.Length == 0 && page.GetImages().Any())
            {
                withoutText.Add(pages[i]);
            }

            texts.Add(text);
        }

        var name = FileNames.Sanitize(baseName);
        if (name.Length == 0)
        {
            name = "text";
        }

        var extension = Extension(parameters.Format);
        var digits = Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        var perPage = parameters.Split == TextPageSplit.FilePerPage;
        IReadOnlyList<string> wanted = perPage ? [.. pages.Select(p => $"{name}-{(p + 1).ToString("D" + digits, CultureInfo.InvariantCulture)}")] : [name];
        var finals = FileNames.MakeUnique(wanted, n => !parameters.Overwrite && File.Exists(Path.Combine(outputFolder, n + extension)))
            .Select(n => Path.Combine(outputFolder, n + extension))
            .ToList();
        IReadOnlyList<string> contents = perPage ? [.. texts.Select(t => t.Length == 0 ? t : t + NewLine)] : [Join(texts, parameters)];
        Directory.CreateDirectory(outputFolder);
        var temporaries = finals.Select(f => Path.Combine(outputFolder, $".{Path.GetFileName(f)}.{Guid.NewGuid():N}.tmp")).ToList();
        try
        {
            for (var i = 0; i < finals.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(temporaries[i], contents[i], Utf8);
            }

            for (var i = 0; i < finals.Count; i++)
            {
                File.Move(temporaries[i], finals[i], overwrite: parameters.Overwrite);
            }
        }
        finally
        {
            foreach (var temporary in temporaries.Where(File.Exists))
            {
                File.Delete(temporary);
            }
        }

        progress?.Report(new OperationProgress(pages.Count, pages.Count, Stage));
        return new ToTextResult(finals, pages.Count, contents.Sum(c => (long)c.Length), withoutText);
    }

    /// <summary>The pages' texts in one file: a page break between pages, or just a blank line.</summary>
    private static string Join(IReadOnlyList<string> texts, ToTextParams parameters)
    {
        var separator = parameters.Split == TextPageSplit.None
            ? NewLine + NewLine
            : parameters.Format == TextFileFormat.Markdown ? NewLine + NewLine + "---" + NewLine + NewLine : NewLine + "\f";
        var text = string.Join(separator, parameters.Split == TextPageSplit.None ? texts.Where(t => t.Length > 0) : texts);
        return text.Length == 0 ? text : text + NewLine;
    }

    /// <summary>The blocks of a page in reading order; empty for a page without text.</summary>
    internal static IReadOnlyList<TextBlock> Blocks(Page page)
    {
        var letters = page.Letters;
        if (!letters.Any(l => !string.IsNullOrWhiteSpace(l.Value)))
        {
            return [];
        }

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        return [.. UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder)];
    }

    /// <summary>The size most of the page's letters have.</summary>
    private static double BodySize(Page page)
    {
        var sizes = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => Math.Round(l.PointSize, 1)).ToList();
        return sizes.Count == 0 ? 0 : sizes.GroupBy(s => s).MaxBy(g => g.Count())!.Key;
    }

    private static void AppendMarkdown(StringBuilder text, TextBlock block, double body)
    {
        var lines = block.TextLines.Select(l => Clean(l.Text)).Where(l => l.Length > 0).ToList();
        var size = Median([.. block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => l.PointSize)]);
        var heading = body > 0 && lines.Count <= 3 ? (size >= body * TitleRatio ? "# " : size >= body * HeadingRatio ? "## " : null) : null;
        if (heading is not null)
        {
            text.Append(heading).Append(Escape(string.Join(' ', lines), atLineStart: false));
            return;
        }

        // One paragraph: the lines joined, a word broken by a hyphen at the end of a line put back together.
        var paragraph = new StringBuilder();
        foreach (var line in lines)
        {
            if (paragraph.Length > 0)
            {
                if (paragraph.Length >= 2 && paragraph[^1] == '-' && char.IsLetter(paragraph[^2]) && char.IsLower(line[0]))
                {
                    paragraph.Length--;
                }
                else
                {
                    paragraph.Append(' ');
                }
            }

            paragraph.Append(line);
        }

        text.Append(Escape(paragraph.ToString(), atLineStart: true));
    }

    /// <summary>
    /// Markdown escapes: characters that would start emphasis, code, links or HTML, and a block marker (heading, quote,
    /// list, rule) at the start of the paragraph.
    /// </summary>
    internal static string Escape(string text, bool atLineStart)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' or '>' or '#' && (c is not '#' and not '>' || escaped.Length == 0))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        var result = escaped.ToString();
        if (atLineStart && result.Length > 1 && result[0] is '-' or '+' or '=' or '_' && result[1] == ' ')
        {
            result = "\\" + result;
        }

        // "1. " or "1) " would start a numbered list.
        var digits = 0;
        while (atLineStart && digits < result.Length && char.IsAsciiDigit(result[digits]))
        {
            digits++;
        }

        return atLineStart && digits > 0 && digits + 1 < result.Length && result[digits] is '.' or ')' && result[digits + 1] == ' '
            ? result[..digits] + "\\" + result[digits..]
            : result;
    }

    /// <summary>Control characters dropped, runs of white space made one space.</summary>
    private static string Clean(string line)
    {
        var text = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            var white = char.IsWhiteSpace(c);
            if (char.IsControl(c) && !white)
            {
                continue;
            }

            if (white)
            {
                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                }

                continue;
            }

            text.Append(c);
        }

        return text.ToString().Trim();
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

    /// <exception cref="InvalidDataException">The page cannot be read.</exception>
    private static Page ReadPage(PigDocument document, int pageIndex)
    {
        try
        {
            return document.GetPage(pageIndex + 1);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Page {pageIndex + 1} cannot be read.", ex);
        }
    }

    private static PigDocument Open(string input, string? password)
    {
        var options = new PigOptions { UseLenientParsing = true, SkipMissingFonts = true, UseActualText = true };
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }

        try
        {
            return PigDocument.Open(input, options);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentPasswordException(DocumentPasswordProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    private void Check(ToTextParams parameters)
    {
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(parameters, new ValidationContext(parameters), problems, validateAllProperties: true))
        {
            throw new OperationValidationException(Id, [.. problems.Select(p => new OperationValidationError(p.MemberNames.FirstOrDefault() ?? string.Empty, p.ErrorMessage ?? string.Empty))]);
        }
    }
}
