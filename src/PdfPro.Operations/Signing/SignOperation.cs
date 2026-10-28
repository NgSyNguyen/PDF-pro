using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;

namespace PdfPro.Operations.Signing;

/// <param name="Request">The signature to add to each file.</param>
/// <param name="Certificate">The signer's certificate with its private key; never written anywhere.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record SignParams(SignRequest Request, [property: JsonIgnore] X509Certificate2 Certificate, bool Overwrite = false)
{
    public override string ToString() => $"SignParams {{ {Request}, Overwrite = {Overwrite} }}";
}

/// <summary>
/// security.sign (<see cref="OperationKind.Export"/>): writes signed copies of files into the output folder — the
/// signature appended as an incremental update (<see cref="PdfSigner"/>). Never part of a session's history: a signed
/// file is a new file, and the certificate is never stored.
/// </summary>
public sealed class SignOperation(ITimestampClient? timestamps = null) : PdfOperation<SignParams>
{
    public const string OperationId = "security.sign";

    public const string Stage = "Stage_Sign";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>
    /// Signs <paramref name="input"/> into <paramref name="output"/> (which may be the input itself): written to a
    /// temporary file of the output's folder first, then moved into place, so a failure leaves the output as it was.
    /// </summary>
    /// <exception cref="SigningException">The document cannot be signed this way.</exception>
    public static async Task SignFileAsync(string input, string output, SignRequest request, X509Certificate2 certificate, ITimestampClient? timestamps, DateTimeOffset signingTime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false), request, certificate, timestamps, signingTime, cancellationToken).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(Path.GetFullPath(output)) ?? throw new ArgumentException("The output has no folder.", nameof(output));
        var temporary = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(signed, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(output))
            {
                File.Replace(temporary, output, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, output);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    protected override OperationValidationResult ValidateCore(SignParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Request is null || string.IsNullOrWhiteSpace(parameters.Request.FieldName))
        {
            errors.Add(new OperationValidationError(nameof(parameters.Request), "A signature field name is needed."));
        }

        if (parameters.Certificate is null || !parameters.Certificate.HasPrivateKey)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Certificate), "A certificate with its private key is needed."));
        }

        if (parameters.Request?.Appearance is { } appearance && (appearance.Page < 1 || appearance.Page > Math.Max(1, document.PageCount)))
        {
            errors.Add(new OperationValidationError(nameof(parameters.Request), $"The page must be between 1 and {document.PageCount}."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, SignParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var validation = Validate(parameters, new DocumentInfo(int.MaxValue));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        Directory.CreateDirectory(context.OutputDirectory);
        var inputs = context.InputPaths.Select(Path.GetFullPath).ToList();
        var names = FileNames.MakeUnique([.. inputs.Select(p => Path.GetFileNameWithoutExtension(p))], name => !parameters.Overwrite && File.Exists(Path.Combine(context.OutputDirectory, name + ".pdf")));
        var outputs = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, inputs.Count, Stage));
            var output = Path.GetFullPath(Path.Combine(context.OutputDirectory, names[i] + ".pdf"));
            if (string.Equals(inputs[i], output, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The output would overwrite the input file; choose another output folder.");
            }

            await SignFileAsync(inputs[i], output, parameters.Request, parameters.Certificate, timestamps, DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
            outputs.Add(output);
        }

        progress.Report(new OperationProgress(context.InputPaths.Count, context.InputPaths.Count, Stage));
        return OperationResult.Success(outputs);
    }
}
