using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Converting;

/// <summary>Why an office document did not become a PDF.</summary>
public enum OfficeConversionFailure
{
    /// <summary>Not a kind of document converted here.</summary>
    Unsupported,

    /// <summary>LibreOffice did not finish in time (it was stopped).</summary>
    Timeout,

    /// <summary>LibreOffice failed or wrote no readable PDF: the document may be damaged or protected.</summary>
    Failed,
}

/// <summary>An office document could not be converted.</summary>
public sealed class OfficeConversionException(OfficeConversionFailure failure, Exception? innerException = null)
    : Exception($"The document could not be converted to PDF: {failure}.", innerException)
{
    public OfficeConversionFailure Failure { get; } = failure;
}

/// <param name="TimeoutSeconds">How long LibreOffice may take for one document before it is stopped.</param>
/// <param name="LibreOfficePath">The soffice program, or its folder, chosen by the user; null to look for it.</param>
/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record OfficeToPdfParams(
    [property: Range(10, 3600)] int TimeoutSeconds = 180,
    string? LibreOfficePath = null,
    bool Overwrite = false);

/// <summary>A converted document.</summary>
/// <param name="Output">The PDF written.</param>
/// <param name="PageCount">Its pages.</param>
public sealed record OfficeConversionResult(string Output, int PageCount);

/// <summary>
/// convert.officeToPdf (<see cref="OperationKind.Export"/>, "Office sang PDF"): Word, Excel, PowerPoint, OpenDocument
/// and RTF documents converted by LibreOffice, run as a separate process (<c>soffice --headless --convert-to pdf</c>,
/// through <see cref="IExternalToolRunner"/>: no shell, a timeout, cancellable). Every run has a user profile of its own
/// (<c>-env:UserInstallation</c>) in a working folder next to the output, so that several conversions run side by side
/// and never touch the user's own LibreOffice settings; the document is copied there under a plain name, and the
/// folder is removed afterwards. The PDF must read back with its pages, and is moved into place only then.
/// </summary>
public sealed class OfficeToPdfOperation(IExternalToolRunner runner, ILibreOfficeLocator locator) : PdfOperation<OfficeToPdfParams>
{
    public const string OperationId = "convert.officeToPdf";

    public const string Stage = "Stage_OfficeToPdf";

    /// <summary>The document types converted (LibreOffice reads more, but these are the ones tested).</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".odt", ".rtf", ".xlsx", ".xls", ".ods", ".pptx", ".ppt", ".odp",
    };

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Whether <paramref name="path"/> is a document this operation converts.</summary>
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Converts <paramref name="input"/> into the PDF <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="LibreOfficeNotFoundException">LibreOffice is not installed (or not where it was said to be).</exception>
    /// <exception cref="OfficeConversionException">The document is not supported, or LibreOffice failed on it.</exception>
    public Task<OfficeConversionResult> ConvertAsync(string input, string output, OfficeToPdfParams parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(parameters);
        Check(parameters);
        if (!IsSupported(input))
        {
            throw new OfficeConversionException(OfficeConversionFailure.Unsupported);
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The document does not exist.", input);
        }

        var soffice = locator.Find(parameters.LibreOfficePath) ?? throw new LibreOfficeNotFoundException();
        return OptimizedFiles.WriteAsync(
            output,
            async (temporary, token) => new OfficeConversionResult(output, await ConvertIntoAsync(soffice, input, temporary, parameters, token).ConfigureAwait(false)),
            cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(OfficeToPdfParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, OfficeToPdfParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            await ConvertAsync(context.InputPaths[i], outputs[i], parameters, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }

    /// <summary>Runs LibreOffice on a copy of the document in a working folder of its own; the PDF's page count.</summary>
    private async Task<int> ConvertIntoAsync(string soffice, string input, string output, OfficeToPdfParams parameters, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetDirectoryName(output)!, $".pdfpro-office-{Guid.NewGuid():N}");
        var source = Path.Combine(work, "in", "document" + Path.GetExtension(input).ToLowerInvariant());
        var converted = Path.Combine(work, "out", "document.pdf");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(converted)!);
            File.Copy(input, source);
            var profile = new Uri(Path.Combine(work, "profile") + Path.DirectorySeparatorChar).AbsoluteUri;
            var request = new ExternalToolRequest(
                soffice,
                [
                    "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nologo", "--nofirststartwizard",
                    "-env:UserInstallation=" + profile,
                    "--convert-to", "pdf",
                    "--outdir", Path.GetDirectoryName(converted)!,
                    source,
                ])
            {
                WorkingDirectory = work,
                Timeout = TimeSpan.FromSeconds(parameters.TimeoutSeconds),
                EnvironmentVariables = Utf8Locale(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()),
            };

            ExternalToolResult result;
            try
            {
                result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalToolNotFoundException ex)
            {
                throw new LibreOfficeNotFoundException(ex);
            }
            catch (ExternalToolTimeoutException ex)
            {
                throw new OfficeConversionException(OfficeConversionFailure.Timeout, ex);
            }

            if (!result.Succeeded || !File.Exists(converted))
            {
                throw new OfficeConversionException(OfficeConversionFailure.Failed, result.Succeeded ? null : new ExternalToolFailedException(result.ToolName, result.ExitCode, ExternalToolResult.Tail(result.StandardError)));
            }

            var pages = PageCount(converted);
            File.Move(converted, output, overwrite: true);
            return pages;
        }
        finally
        {
            Remove(work);
        }
    }

    /// <summary>
    /// Outside Windows LibreOffice turns paths and file URIs into bytes with the locale's character set: under a POSIX
    /// locale a path with "ệ" in it cannot be made, and soffice waits for ever instead of failing. When the locale is
    /// not UTF-8 already, soffice runs with the character set of C.UTF-8 (its language and formats are left alone,
    /// unless LC_ALL had overridden them). Windows paths are UTF-16: nothing changes there.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> Utf8Locale(Func<string, string?> environment, bool windows)
    {
        if (windows)
        {
            return new Dictionary<string, string?>();
        }

        var all = environment("LC_ALL");
        var characters = new[] { all, environment("LC_CTYPE"), environment("LANG") }.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (characters is not null && (characters.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || characters.Contains("utf8", StringComparison.OrdinalIgnoreCase)))
        {
            return new Dictionary<string, string?>();
        }

        return new Dictionary<string, string?> { [string.IsNullOrEmpty(all) ? "LC_CTYPE" : "LC_ALL"] = "C.UTF-8" };
    }

    /// <summary>The pages of what LibreOffice wrote; a file that is not a readable PDF with pages is a failure.</summary>
    private static int PageCount(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount > 0 ? document.PageCount : throw new OfficeConversionException(OfficeConversionFailure.Failed);
        }
        catch (Exception ex) when (ex is not OfficeConversionException and not OutOfMemoryException and not IOException)
        {
            throw new OfficeConversionException(OfficeConversionFailure.Failed, ex);
        }
    }

    /// <summary>The working folder goes; LibreOffice may still hold a file of its profile for a moment.</summary>
    private static void Remove(string folder)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    private void Check(OfficeToPdfParams parameters)
    {
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(parameters, new ValidationContext(parameters), problems, validateAllProperties: true))
        {
            throw new OperationValidationException(Id, [.. problems.Select(p => new OperationValidationError(p.MemberNames.FirstOrDefault() ?? string.Empty, p.ErrorMessage ?? string.Empty))]);
        }
    }
}
