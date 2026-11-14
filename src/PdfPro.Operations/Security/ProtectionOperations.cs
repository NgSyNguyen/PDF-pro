using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Security;
using PdfPro.Core.Splitting;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Security;

/// <param name="Security">The protection to give the files.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record ProtectParams(DocumentSecurity Security, bool Overwrite = false);

/// <summary>Takes the protection off: the owner password of each protected file comes with its input.</summary>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record UnprotectParams(bool Overwrite = false);

/// <summary>One of the files could not be protected or unprotected; <see cref="FileIndex"/> says which (0-based).</summary>
public sealed class ProtectionFileException(int fileIndex, Exception innerException)
    : Exception("A file could not be written.", innerException)
{
    public int FileIndex { get; } = fileIndex;
}

/// <summary>
/// security.encrypt (<see cref="OperationKind.Export"/>): writes protected copies of files — AES-256 or AES-128, an
/// open password, an owner password, permissions — into the output folder under the same names. A file that is
/// already protected needs its owner password. Never part of a session's history, so passwords are never stored.
/// </summary>
public sealed class ProtectDocumentOperation : PdfOperation<ProtectParams>
{
    public const string OperationId = "security.encrypt";

    public const string Stage = "Stage_Protect";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes a protected copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="DocumentPasswordException">The input is protected and its owner password was not given.</exception>
    public static Task WriteAsync(string input, string? inputPassword, string output, DocumentSecurity security, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(security);
        return ProtectionFiles.WriteOneAsync(input, inputPassword, output, document => EncryptionWriter.Protect(document, security), cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(ProtectParams parameters, DocumentInfo document)
        => parameters.Security is null
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Security), "The protection is needed."))
            : parameters.Security.Problem() is { } problem
                ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Security), problem))
                : OperationValidationResult.Success;

    protected override Task<OperationResult> ExecuteCoreAsync(OperationContext context, ProtectParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var validation = Validate(parameters, new DocumentInfo(1));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        return ProtectionFiles.WriteAllAsync(context, parameters.Overwrite, document => EncryptionWriter.Protect(document, parameters.Security), progress, Stage, cancellationToken);
    }
}

/// <summary>
/// security.decrypt (<see cref="OperationKind.Export"/>): writes copies of protected files without protection — only
/// with the owner password of each (the open password does not lift the author's restrictions).
/// </summary>
public sealed class UnprotectDocumentOperation : PdfOperation<UnprotectParams>
{
    public const string OperationId = "security.decrypt";

    public const string Stage = "Stage_Unprotect";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes an unprotected copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="DocumentPasswordException">The owner password is missing or wrong.</exception>
    public static Task WriteAsync(string input, string? ownerPassword, string output, CancellationToken cancellationToken)
        => ProtectionFiles.WriteOneAsync(input, ownerPassword, output, EncryptionWriter.Unprotect, cancellationToken);

    protected override Task<OperationResult> ExecuteCoreAsync(OperationContext context, UnprotectParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => ProtectionFiles.WriteAllAsync(context, parameters.Overwrite, EncryptionWriter.Unprotect, progress, Stage, cancellationToken);
}

/// <summary>Writes changed copies through temporary files in the output folder: nothing appears unless every file succeeds.</summary>
internal static class ProtectionFiles
{
    public static Task WriteOneAsync(string input, string? password, string output, Action<PdfDocument> change, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        return Task.Run(
            () =>
            {
                var target = Path.GetFullPath(output);
                var temp = TempNear(target);
                try
                {
                    Write(input, password, temp, change, cancellationToken);
                    File.Move(temp, target, overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }
            },
            cancellationToken);
    }

    public static Task<OperationResult> WriteAllAsync(OperationContext context, bool overwrite, Action<PdfDocument> change, IProgress<OperationProgress> progress, string stage, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                var inputs = context.InputPaths;
                if (inputs.Count == 0)
                {
                    throw new ArgumentException("Give at least one file.", nameof(context));
                }

                var folder = Path.GetFullPath(context.OutputDirectory);
                Directory.CreateDirectory(folder);
                var names = FileNames.MakeUnique([.. inputs.Select(Path.GetFileNameWithoutExtension)!], name => !overwrite && File.Exists(Path.Combine(folder, name + ".pdf")));
                var outputs = names.Select(n => Path.Combine(folder, n + ".pdf")).ToArray();
                if (outputs.Any(output => inputs.Any(input => string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException("A copy would replace one of the files; choose another output folder.");
                }

                var temps = new List<string>();
                try
                {
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var temp = TempNear(outputs[i]);
                        temps.Add(temp);
                        try
                        {
                            Write(inputs[i], context.Passwords?.For(inputs[i]), temp, change, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw new ProtectionFileException(i, ex);
                        }

                        progress.Report(new OperationProgress(i + 1, inputs.Count, stage));
                    }

                    for (var i = 0; i < inputs.Count; i++)
                    {
                        File.Move(temps[i], outputs[i], overwrite);
                    }

                    temps.Clear();
                }
                finally
                {
                    temps.ForEach(TryDelete);
                }

                return OperationResult.Success(outputs);
            },
            cancellationToken);

    private static void Write(string input, string? password, string temp, Action<PdfDocument> change, CancellationToken cancellationToken)
    {
        using var document = EncryptionWriter.OpenForChange(input, password);
        change(document);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Materializing.PdfWriting.Save(document, stream);
        stream.Flush(flushToDisk: true);
    }

    private static string TempNear(string target)
        => Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.pdfpro-security.tmp");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is hidden and named as such.
        }
    }
}
