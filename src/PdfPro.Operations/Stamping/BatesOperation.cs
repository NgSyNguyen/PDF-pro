using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Stamping;

/// <summary>The numbered copies written by Bates numbering, and the number the next page would get.</summary>
public sealed record BatesResult(IReadOnlyList<string> OutputPaths, IReadOnlyList<BatesRange> Ranges, long NextNumber);

/// <summary>One of the files could not be read or written; <see cref="FileIndex"/> says which (0-based, in input order).</summary>
public sealed class BatesFileException(int fileIndex, Exception innerException)
    : Exception("A file could not be numbered.", innerException)
{
    public int FileIndex { get; } = fileIndex;
}

/// <summary>
/// stamp.bates (<see cref="OperationKind.Export"/>): numbers every page of several files with one running Bates number
/// ("HS-000123") — no gaps and no repeats from one file to the next — and writes numbered copies to an output folder
/// under the same names. Nothing is written unless every file succeeds; the inputs are never changed.
/// </summary>
public sealed class BatesOperation : PdfOperation<BatesParams>
{
    public const string OperationId = "stamp.bates";

    public const int MaxAffixLength = 50;

    public const string Stage = "Stage_Bates";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Numbers <paramref name="inputs"/> in order and writes the copies into <paramref name="outputFolder"/>.</summary>
    /// <exception cref="BatesFileException">A file could not be read or written.</exception>
    public async Task<BatesResult> WriteAsync(IReadOnlyList<string> inputs, BatesParams parameters, string outputFolder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(progress);
        var validation = Validate(parameters, new DocumentInfo(1));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        if (inputs.Count == 0)
        {
            throw new ArgumentException("Give at least one file.", nameof(inputs));
        }

        return await Task.Run(() => Write(inputs, parameters, Path.GetFullPath(outputFolder), progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    protected override OperationValidationResult ValidateCore(BatesParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            StampContent.CheckStyle(parameters.Style, nameof(parameters.Style)),
            (parameters.Prefix + parameters.Suffix).Any(char.IsControl)
                ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Prefix), "Prefix and suffix cannot contain control characters."))
                : OperationValidationResult.Success);

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, BatesParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var result = await WriteAsync(context.InputPaths, parameters, context.OutputDirectory, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(result.OutputPaths);
    }

    private BatesResult Write(IReadOnlyList<string> inputs, BatesParams parameters, string folder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var counts = new int[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            try
            {
                using var document = PdfReader.Open(inputs[i], PdfDocumentOpenMode.Import);
                counts[i] = document.PageCount;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BatesFileException(i, ex);
            }
        }

        var ranges = BatesNumbering.Plan(counts, parameters.StartNumber);
        Directory.CreateDirectory(folder);
        var stems = inputs.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
        var names = FileNames.MakeUnique(stems, name => !parameters.Overwrite && File.Exists(Path.Combine(folder, name + ".pdf")));
        var outputs = names.Select(n => Path.Combine(folder, n + ".pdf")).ToArray();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs.Any(input => string.Equals(Path.GetFullPath(input), outputs[i], StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A numbered copy would replace one of the files; choose another output folder.");
            }
        }

        var format = new BatesFormat(parameters.Prefix, parameters.Digits, parameters.Suffix);
        var style = parameters.Style ?? BatesParams.DefaultStyle;
        var temps = new List<string>();
        var total = counts.Sum();
        var done = 0;
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temp = Path.Combine(folder, $".{names[i]}.{Guid.NewGuid():N}.pdfpro-bates.tmp");
                temps.Add(temp);
                try
                {
                    using var document = DocumentContentOperation<BatesParams>.OpenForChange(inputs[i]);
                    var range = ranges[i];
                    var offset = done;
                    var fileProgress = new Forward(p => progress.Report(new OperationProgress(offset + p.Completed, total, Stage)));
                    var zone = new TextZone(parameters.Anchor, page => format.Label(range.NumberOf(page)));
                    TextZones.Stamp(document, [.. Enumerable.Range(0, counts[i])], [zone], style, parameters.MarginX, parameters.MarginY, StampContent.BatesLayer, fileProgress, Stage, cancellationToken);
                    using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    document.Save(stream, closeStream: false);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not BatesFileException)
                {
                    throw new BatesFileException(i, ex);
                }

                done += counts[i];
                progress.Report(new OperationProgress(done, total, Stage));
            }

            for (var i = 0; i < inputs.Count; i++)
            {
                File.Move(temps[i], outputs[i], overwrite: parameters.Overwrite);
            }

            temps.Clear();
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

        return new BatesResult(outputs, ranges, ranges[^1].Last + 1);
    }

    /// <summary>Reports straight away on the calling thread (a file's pages count towards the whole run).</summary>
    private sealed class Forward(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
