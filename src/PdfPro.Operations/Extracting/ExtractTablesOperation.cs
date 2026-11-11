using System.ComponentModel.DataAnnotations;
using System.Globalization;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Core.Tables;
using PdfPro.Operations.Security;

namespace PdfPro.Operations.Extracting;

/// <summary>Where the tables go.</summary>
public enum TableFileFormat
{
    /// <summary>One Excel workbook, a sheet per table.</summary>
    Xlsx,

    /// <summary>One CSV file per table (UTF-8 with a BOM).</summary>
    Csv,
}

/// <summary>The separator of CSV files: Excel splits on the list separator of the computer's region.</summary>
public enum CsvSeparator
{
    Comma,
    Semicolon,
    Tab,
}

/// <param name="Format">Excel or CSV.</param>
/// <param name="Pages">Page range ("1-3, 8"); null or empty means all pages. Ignored with <paramref name="Regions"/>.</param>
/// <param name="Regions">Where to read tables (areas the user drew); null to find them on <paramref name="Pages"/>.</param>
/// <param name="Separator">CSV only.</param>
/// <param name="Numbers">How numbers are written in the document, so Excel gets them as numbers (Excel only).</param>
/// <param name="SheetName">A sheet's name: {0} the table's number, {1} its page.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2)").</param>
public sealed record ExtractTablesParams(
    TableFileFormat Format = TableFileFormat.Xlsx,
    string? Pages = null,
    IReadOnlyList<TableRegion>? Regions = null,
    CsvSeparator Separator = CsvSeparator.Comma,
    NumberReading Numbers = NumberReading.Text,
    [property: Required] string SheetName = "Table {0} (page {1})",
    bool Overwrite = false);

/// <summary>What was written: the files (none when no table was found) and the tables, in order.</summary>
public sealed record ExtractTablesResult(IReadOnlyList<string> Files, IReadOnlyList<ExtractedTable> Tables);

/// <summary>
/// extract.tables (<see cref="OperationKind.Export"/>, "Trích bảng"): the tables of pages (<see cref="TableExtractor"/>)
/// written as one Excel workbook with a sheet per table, or one CSV file per table, for Excel to open with its Vietnamese
/// intact. Files are written under temporary names and renamed when all are done.
/// </summary>
public sealed class ExtractTablesOperation : PdfOperation<ExtractTablesParams>
{
    public const string OperationId = "extract.tables";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    public static string Extension(TableFileFormat format) => format == TableFileFormat.Csv ? ".csv" : ".xlsx";

    public static char SeparatorChar(CsvSeparator separator) => separator switch
    {
        CsvSeparator.Semicolon => ';',
        CsvSeparator.Tab => '\t',
        _ => ',',
    };

    /// <summary>Finds the tables of <paramref name="input"/> (or reads the regions asked) and writes them into <paramref name="outputFolder"/>.</summary>
    /// <exception cref="DocumentPasswordException">The file is protected and the password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    /// <exception cref="OperationValidationException">The parameters or the page range are not valid.</exception>
    public async Task<ExtractTablesResult> WriteAsync(
        string input,
        string? password,
        string outputFolder,
        string baseName,
        ExtractTablesParams parameters,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(parameters);
        IReadOnlyList<ExtractedTable> tables;
        if (parameters.Regions is { } regions)
        {
            tables = await TableExtractor.ReadAsync(input, password, regions, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var pageCount = await Task.Run(() => TableExtractor.CountPages(input, password), cancellationToken).ConfigureAwait(false);
            if (!PageRanges.TryParse(string.IsNullOrWhiteSpace(parameters.Pages) ? "1-" + pageCount.ToString(CultureInfo.InvariantCulture) : parameters.Pages, pageCount, out var pages, out _) || pages.Count == 0)
            {
                throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Pages), $"The document has {pageCount} pages.")]);
            }

            tables = await TableExtractor.FindAsync(input, password, pages, progress, cancellationToken).ConfigureAwait(false);
        }

        var files = await WriteTablesAsync(tables, outputFolder, baseName, parameters, cancellationToken).ConfigureAwait(false);
        return new ExtractTablesResult(files, tables);
    }

    /// <summary>
    /// Writes tables already read (as the user checked them): "name.xlsx", or "name.csv" for one table and "name-1.csv"…
    /// for several. Nothing is written for no table.
    /// </summary>
    public Task<IReadOnlyList<string>> WriteTablesAsync(IReadOnlyList<ExtractedTable> tables, string outputFolder, string baseName, ExtractTablesParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(parameters);
        var validation = Validate(parameters, new DocumentInfo(Math.Max(1, tables.Count == 0 ? 1 : tables.Max(t => t.Page) + 1)));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        return Task.Run<IReadOnlyList<string>>(() => Write(tables, outputFolder, baseName, parameters, cancellationToken), cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(ExtractTablesParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Regions is { } regions && regions.Any(r => r.Page < 0 || (r.Area is { } area && (area.Width <= 0 || area.Height <= 0))))
        {
            errors.Add(new OperationValidationError(nameof(parameters.Regions), "A region is outside the document or empty."));
        }

        if (!parameters.SheetName.Contains("{0}", StringComparison.Ordinal))
        {
            errors.Add(new OperationValidationError(nameof(parameters.SheetName), "The sheet name must hold the table's number ({0})."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed([.. errors]);
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ExtractTablesParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var input in context.InputPaths)
        {
            var result = await WriteAsync(input, context.Passwords?.For(input), context.OutputDirectory, Path.GetFileNameWithoutExtension(input), parameters, progress, cancellationToken).ConfigureAwait(false);
            files.AddRange(result.Files);
        }

        return OperationResult.Success(files);
    }

    private static List<string> Write(IReadOnlyList<ExtractedTable> tables, string outputFolder, string baseName, ExtractTablesParams parameters, CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return [];
        }

        var name = FileNames.Sanitize(baseName);
        if (name.Length == 0)
        {
            name = "tables";
        }

        var extension = Extension(parameters.Format);
        var csv = parameters.Format == TableFileFormat.Csv;
        IReadOnlyList<string> wanted = csv && tables.Count > 1
            ? [.. Enumerable.Range(1, tables.Count).Select(i => $"{name}-{i.ToString(CultureInfo.InvariantCulture)}")]
            : [name];
        var finals = FileNames.MakeUnique(wanted, n => !parameters.Overwrite && File.Exists(Path.Combine(outputFolder, n + extension)))
            .Select(n => Path.Combine(outputFolder, n + extension))
            .ToList();
        Directory.CreateDirectory(outputFolder);
        var temporaries = finals.Select(f => Path.Combine(outputFolder, $".{Path.GetFileName(f)}.{Guid.NewGuid():N}.tmp")).ToList();
        try
        {
            if (csv)
            {
                var separator = SeparatorChar(parameters.Separator);
                for (var i = 0; i < tables.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllBytes(temporaries[i], CsvWriter.Write(tables[i].Rows, separator));
                }
            }
            else
            {
                var sheets = tables
                    .Select((t, i) => new SheetContent(string.Format(CultureInfo.CurrentCulture, parameters.SheetName, i + 1, t.Page + 1), t.Rows))
                    .ToList();
                using var stream = new FileStream(temporaries[0], FileMode.CreateNew, FileAccess.Write);
                XlsxWriter.Write(stream, sheets, parameters.Numbers);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
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

        return finals;
    }
}
