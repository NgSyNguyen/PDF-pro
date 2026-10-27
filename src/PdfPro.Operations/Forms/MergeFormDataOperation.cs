using System.ComponentModel.DataAnnotations;
using System.Globalization;
using PdfPro.Core.Documents;
using PdfPro.Core.Forms;
using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;
using PdfPro.Core.Tables;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Forms;

/// <param name="DataPath">The table: CSV (.csv, .txt) or an Excel workbook (.xlsx); its first row names the fields.</param>
/// <param name="FileNameColumn">The column naming each file; null or a blank cell gives "form name (row)".</param>
/// <param name="Overwrite">Files of the same name in the output folder are replaced (else new names are found).</param>
public sealed record MergeFormParams([property: Required] string DataPath, string? FileNameColumn = null, bool Overwrite = false);

/// <summary>A value of the table that does not fit its field (it is left empty in that file).</summary>
/// <param name="Row">The row of the table, 1-based, header not counted.</param>
/// <param name="Column">The column it is in.</param>
public sealed record MergeProblem(int Row, string Column);

/// <summary>What a merge wrote, the columns that are not fields of the form, and the values that did not fit.</summary>
public sealed record MergeFormResult(IReadOnlyList<string> OutputPaths, IReadOnlyList<string> UnknownColumns, IReadOnlyList<MergeProblem> Problems);

/// <summary>The data file cannot be read; <see cref="Reason"/> says why (in English, for logs).</summary>
public sealed class MergeDataException(string reason, Exception? innerException = null) : Exception(reason, innerException)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// forms.merge (<see cref="OperationKind.Export"/>): fills the form of a document once per row of a table (CSV or Excel)
/// and writes one file per row into an output folder, named by a column. Columns are matched to fields by name (also
/// ignoring case); a check box takes yes/no words, a list several choices separated by ";". Nothing is written unless
/// every file succeeds; the document itself is never changed.
/// </summary>
public sealed class MergeFormDataOperation : PdfOperation<MergeFormParams>
{
    public const string OperationId = "forms.merge";

    public const string Stage = "Stage_MergeForm";

    /// <summary>The largest data file read (bytes).</summary>
    public const long MaxDataBytes = 50L * 1024 * 1024;

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Reads a data file by its extension.</summary>
    /// <exception cref="MergeDataException">It cannot be read, is not CSV or a workbook, or is too big.</exception>
    public static TabularData ReadTable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxDataBytes)
            {
                throw new MergeDataException("The data file is too big.");
            }

            if (string.Equals(info.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                return XlsxTable.Read(stream);
            }

            return CsvTable.Read(File.ReadAllBytes(path));
        }
        catch (TableFormatException ex)
        {
            throw new MergeDataException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MergeDataException("The data file cannot be opened.", ex);
        }
    }

    /// <summary>Fills <paramref name="templatePath"/> once per row of <paramref name="table"/> into <paramref name="outputFolder"/>.</summary>
    /// <exception cref="InvalidDataException">The document cannot be read or has no form.</exception>
    public async Task<MergeFormResult> WriteAsync(string templatePath, TabularData table, MergeFormParams parameters, string outputFolder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(progress);
        var validation = Validate(parameters, new DocumentInfo(1));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        return await Task.Run(() => Write(templatePath, table, parameters, Path.GetFullPath(outputFolder), progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    protected override OperationValidationResult ValidateCore(MergeFormParams parameters, DocumentInfo document)
        => string.IsNullOrWhiteSpace(parameters.DataPath)
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.DataPath), "Choose the data file."))
            : OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, MergeFormParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var table = ReadTable(parameters.DataPath);
        var result = await WriteAsync(context.InputPaths[0], table, parameters, context.OutputDirectory, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(result.OutputPaths);
    }

    private static MergeFormResult Write(string templatePath, TabularData table, MergeFormParams parameters, string folder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        byte[] template;
        IReadOnlyList<FormField> fields;
        try
        {
            template = File.ReadAllBytes(templatePath);
            using var document = PdfReader.Open(new MemoryStream(template), PdfDocumentOpenMode.Import);
            fields = FormReader.Read(document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }

        if (!fields.Any(f => f.IsFillable))
        {
            throw new InvalidDataException("The document has no form fields to fill.");
        }

        var nameColumn = parameters.FileNameColumn is { Length: > 0 } column ? table.IndexOf(column) : -1;
        var map = table.Columns.Select(c => FormData.Find(fields, c)).ToList();
        var unknown = table.Columns.Where((c, i) => map[i] is null && i != nameColumn).ToList();
        var stem = Path.GetFileNameWithoutExtension(templatePath);
        var wanted = table.Rows.Select((row, i) =>
            nameColumn >= 0 && FileNames.Sanitize(row[nameColumn]) is { Length: > 0 } name
                ? name
                : string.Create(CultureInfo.InvariantCulture, $"{stem} ({i + 1})")).ToList();
        Directory.CreateDirectory(folder);
        var names = FileNames.MakeUnique(wanted, name => !parameters.Overwrite && File.Exists(Path.Combine(folder, name + ".pdf")));
        var outputs = names.Select(n => Path.Combine(folder, n + ".pdf")).ToList();
        if (outputs.Any(o => string.Equals(o, Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A filled copy would replace the document; choose another output folder.");
        }

        var problems = new List<MergeProblem>();
        var temps = new List<string>();
        try
        {
            for (var r = 0; r < table.Rows.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new OperationProgress(r, table.Rows.Count, Stage));
                var values = new List<FormValue>();
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    if (map[c] is not { } field)
                    {
                        continue;
                    }

                    var cell = table.Rows[r][c];
                    var data = field.Kind == FormFieldKind.ListBox ? cell.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [cell];
                    if (FormData.Value(field, data) is { } value)
                    {
                        values.Add(value);
                    }
                    else if (field.IsFillable)
                    {
                        problems.Add(new MergeProblem(r + 1, table.Columns[c]));
                    }
                }

                var temp = Path.Combine(folder, $".{names[r]}.{Guid.NewGuid():N}.pdfpro-merge.tmp");
                temps.Add(temp);
                using (var document = PdfReader.Open(new MemoryStream(template), PdfDocumentOpenMode.Modify))
                {
                    FormWriter.SetValues(document, [.. values.DistinctBy(v => v.Name)]);
                    using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    document.Save(stream, closeStream: false);
                    stream.Flush(flushToDisk: true);
                }
            }

            for (var r = 0; r < outputs.Count; r++)
            {
                File.Move(temps[r], outputs[r], overwrite: parameters.Overwrite);
            }

            temps.Clear();
            progress.Report(new OperationProgress(table.Rows.Count, table.Rows.Count, Stage));
        }
        finally
        {
            foreach (var temp in temps)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best effort: a leftover temporary file is hidden and named as such.
                }
            }
        }

        return new MergeFormResult(outputs, unknown, problems);
    }
}
