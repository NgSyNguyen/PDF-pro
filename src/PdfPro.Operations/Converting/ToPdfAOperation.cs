using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting.PdfA;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Converting;

/// <summary>
/// convert.toPdfA (<see cref="OperationKind.Export"/>, "PDF/A"): a copy of a document made PDF/A-2b (the default) or
/// PDF/A-1b, the ISO standard for keeping documents readable in the long run. The document is opened with PDFsharp (a
/// protected one with its owner password, and the protection goes), changed where PDF/A asks for it
/// (<see cref="PdfAStructure"/>: scripts, forbidden actions, attachments, multimedia, comments, form fields, layers,
/// LZW, image alternates, PostScript, transfer functions, object metadata; an sRGB output intent), given document
/// information and XMP that agree (<see cref="PdfAMetadata"/>), written as PDF 1.4 (A-1) or 1.7 (A-2) through a
/// temporary file, and read back. What PDF_Pro's own checks still find (<see cref="PdfAInspection"/>) is reported;
/// a full validation is veraPDF's.
/// </summary>
public sealed class ToPdfAOperation : PdfOperation<ToPdfAParams>
{
    public const string OperationId = "convert.toPdfA";

    public const string Stage = "Stage_ToPdfA";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Converts <paramref name="input"/> into the PDF/A file <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="DocumentPasswordException">The document is protected and the password is missing, wrong, or not the owner's.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read, or what was written does not read back.</exception>
    public Task<PdfAReport> ConvertAsync(string input, string? password, string output, ToPdfAParams parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The document does not exist.", input);
        }

        return OptimizedFiles.WriteAsync(
            output,
            (temporary, token) => Task.Run(() => Convert(input, password, temporary, output, parameters.Level, token), token),
            cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(ToPdfAParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, ToPdfAParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            var input = context.InputPaths[i];
            await ConvertAsync(input, context.Passwords?.For(input), outputs[i], parameters, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }

    private static PdfAReport Convert(string input, string? password, string temporary, string output, PdfALevel level, CancellationToken cancellationToken)
    {
        var changes = new PdfAChanges();
        if (IsEncrypted(input))
        {
            changes.Add(PdfAFix.Decrypted);
        }

        using var document = EncryptionWriter.OpenForChange(input, password);
        cancellationToken.ThrowIfCancellationRequested();
        PdfAStructure.Apply(document, level, changes, cancellationToken);
        PdfAMetadata.Write(document, level, DateTimeOffset.Now);
        changes.Add(PdfAFix.Metadata);
        document.Version = level == PdfALevel.A1B ? 14 : 17;

        // Else PDFsharp gives every page a transparency group when it saves, which PDF/A-1 forbids.
        document.Options.ColorMode = PdfColorMode.Undefined;
        var problems = PdfAInspection.Problems(document, level);
        var pages = document.PageCount;
        cancellationToken.ThrowIfCancellationRequested();
        using (var file = File.Create(temporary))
        {
            PdfWriting.Save(document, file);
        }

        try
        {
            using var written = PdfReader.Open(temporary, PdfDocumentOpenMode.Import);
            if (written.PageCount != pages)
            {
                throw new InvalidDataException("The converted document lost pages.");
            }
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException)
        {
            throw new InvalidDataException("The converted document does not read back.", ex);
        }

        return new PdfAReport(output, level, pages, changes.ToList(), problems);
    }

    /// <summary>Whether a trailer of the file names an /Encrypt dictionary (looked for at both ends of the file).</summary>
    private static bool IsEncrypted(string path)
    {
        const int Window = 1024 * 1024;
        using var file = File.OpenRead(path);
        var head = new byte[(int)Math.Min(file.Length, 64 * 1024)];
        file.ReadExactly(head);
        var tail = new byte[(int)Math.Min(file.Length, Window)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var marker = "/Encrypt"u8;
        return head.AsSpan().IndexOf(marker) >= 0 || tail.AsSpan().IndexOf(marker) >= 0;
    }
}
