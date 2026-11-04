using PdfPro.Core.Documents;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Writer;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PigOptions = UglyToad.PdfPig.ParsingOptions;

namespace PdfPro.Operations.Optimizing;

/// <summary>How a damaged file was read back.</summary>
public enum RepairMethod
{
    /// <summary>PDFium read it (it rebuilds broken cross-reference tables) and wrote it anew.</summary>
    Pdfium,

    /// <summary>qpdf recovered it.</summary>
    Qpdf,

    /// <summary>Its objects were intact: a new cross-reference section was built for them.</summary>
    CrossReference,

    /// <summary>PdfPig read it leniently and its pages were copied into a new file.</summary>
    PdfPig,
}

/// <summary>Why one way of repairing did not work.</summary>
public enum RepairAttemptFailure
{
    /// <summary>The way could not read the file.</summary>
    Unreadable,

    /// <summary>It wrote something that does not open with every page.</summary>
    BadResult,

    /// <summary>The tool is not installed (qpdf).</summary>
    Missing,
}

/// <summary>What repairing a file did: the way that worked, after the ones that did not, and the pages it has now.</summary>
public sealed record RepairReport(RepairMethod Method, int PageCount, IReadOnlyList<(RepairMethod Method, RepairAttemptFailure Failure)> Failed, bool HadWarnings);

/// <summary>No way of repairing could read the file.</summary>
public sealed class RepairFailedException(IReadOnlyList<(RepairMethod Method, RepairAttemptFailure Failure)> attempts)
    : Exception("The file could not be repaired.")
{
    public IReadOnlyList<(RepairMethod Method, RepairAttemptFailure Failure)> Attempts { get; } = attempts;
}

/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record RepairParams(bool Overwrite = false);

/// <summary>
/// optimize.repair (<see cref="OperationKind.Export"/>, "Sửa tệp hỏng"): damaged files read back in turn by PDFium
/// (opened and written anew), qpdf (cross-reference recovery), a cross-reference section rebuilt from the objects
/// found (<see cref="CrossReferenceRebuilder"/>, for files that lost theirs and their trailer) and PdfPig (lenient
/// parsing, pages copied); the first way whose result opens in PDFium with pages is kept, and the report says which.
/// </summary>
public sealed class RepairOperation(PdfiumGate gate, Qpdf? qpdf = null) : PdfOperation<RepairParams>
{
    public const string OperationId = "optimize.repair";

    public const string Stage = "Stage_Repair";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes a repaired copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="RepairFailedException">No way could read the file.</exception>
    public Task<RepairReport> RepairAsync(string input, string output, CancellationToken cancellationToken)
        => OptimizedFiles.WriteAsync(output, (temporary, token) => RepairIntoAsync(input, temporary, token), cancellationToken);

    protected override OperationValidationResult ValidateCore(RepairParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, RepairParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            await RepairAsync(context.InputPaths[i], outputs[i], cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }

    private async Task<RepairReport> RepairIntoAsync(string input, string output, CancellationToken cancellationToken)
    {
        var failed = new List<(RepairMethod, RepairAttemptFailure)>();

        // PDFium: it reconstructs damaged cross-reference tables while opening.
        try
        {
            await gate.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(gate, input);
                    using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
                    document.SaveCopy(stream);
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);
            if (await PagesAsync(output, cancellationToken).ConfigureAwait(false) is > 0 and var pages && OpensForWriting(output))
            {
                return new RepairReport(RepairMethod.Pdfium, pages, failed, HadWarnings: false);
            }

            failed.Add((RepairMethod.Pdfium, RepairAttemptFailure.BadResult));
        }
        catch (Exception ex) when (ex is PdfDocumentException or IOException)
        {
            failed.Add((RepairMethod.Pdfium, RepairAttemptFailure.Unreadable));
        }

        // qpdf: its own recovery of the cross-reference table.
        if (qpdf is null)
        {
            failed.Add((RepairMethod.Qpdf, RepairAttemptFailure.Missing));
        }
        else
        {
            try
            {
                var warnings = await qpdf.RewriteAsync(input, output, cancellationToken).ConfigureAwait(false);
                if (await PagesAsync(output, cancellationToken).ConfigureAwait(false) is > 0 and var pages)
                {
                    return new RepairReport(RepairMethod.Qpdf, pages, failed, warnings);
                }

                failed.Add((RepairMethod.Qpdf, RepairAttemptFailure.BadResult));
            }
            catch (ExternalToolNotFoundException)
            {
                failed.Add((RepairMethod.Qpdf, RepairAttemptFailure.Missing));
            }
            catch (Exception ex) when (ex is QpdfException or ExternalToolException or IOException)
            {
                failed.Add((RepairMethod.Qpdf, RepairAttemptFailure.Unreadable));
            }
        }

        // The objects are intact but the cross-reference section and trailer are gone: build them again.
        try
        {
            if (CrossReferenceRebuilder.Rebuild(await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false)) is { } rebuilt)
            {
                await File.WriteAllBytesAsync(output, rebuilt, cancellationToken).ConfigureAwait(false);
                if (await PagesAsync(output, cancellationToken).ConfigureAwait(false) is > 0 and var pages)
                {
                    // Written anew by PDFium too when it can, so that it is a plain well-formed file.
                    return new RepairReport(RepairMethod.CrossReference, pages, failed, HadWarnings: false);
                }

                failed.Add((RepairMethod.CrossReference, RepairAttemptFailure.BadResult));
            }
            else
            {
                failed.Add((RepairMethod.CrossReference, RepairAttemptFailure.Unreadable));
            }
        }
        catch (IOException)
        {
            failed.Add((RepairMethod.CrossReference, RepairAttemptFailure.Unreadable));
        }

        // PdfPig, lenient: every page it can read copied into a new file.
        try
        {
            var bytes = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
            byte[] rebuilt;
            using (var damaged = PigDocument.Open(bytes, new PigOptions { UseLenientParsing = true, SkipMissingFonts = true }))
            using (var builder = new PdfDocumentBuilder())
            {
                for (var page = 1; page <= damaged.NumberOfPages; page++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    builder.AddPage(damaged, page);
                }

                rebuilt = builder.Build();
            }

            await File.WriteAllBytesAsync(output, rebuilt, cancellationToken).ConfigureAwait(false);
            if (await PagesAsync(output, cancellationToken).ConfigureAwait(false) is > 0 and var pages)
            {
                return new RepairReport(RepairMethod.PdfPig, pages, failed, HadWarnings: false);
            }

            failed.Add((RepairMethod.PdfPig, RepairAttemptFailure.BadResult));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            failed.Add((RepairMethod.PdfPig, RepairAttemptFailure.Unreadable));
        }

        throw new RepairFailedException(failed);
    }

    /// <summary>The pages PDFium finds in a written file, or 0 when it does not open.</summary>
    private async Task<int> PagesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await gate.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(gate, path);
                    return document.PageCount;
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PdfDocumentException)
        {
            return 0;
        }
    }

    /// <summary>PDFsharp (what every later edit writes with) can read it too.</summary>
    private static bool OpensForWriting(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
