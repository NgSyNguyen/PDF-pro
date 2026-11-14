using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf.IO;
using PdfPro.Operations.Materializing;

namespace PdfPro.Operations.Converting;

/// <summary>Why an office document did not become a PDF.</summary>
public enum OfficeConversionFailure
{
    /// <summary>Not a kind of document converted here.</summary>
    Unsupported,

    /// <summary>The converting program did not finish in time (it was stopped).</summary>
    Timeout,

    /// <summary>The converting program failed or wrote no readable PDF: the document may be damaged or protected.</summary>
    Failed,

    /// <summary>The document is protected by a password (the word processor said so).</summary>
    Protected,
}

/// <summary>Which program converts.</summary>
public enum OfficeConverter
{
    /// <summary>
    /// Word documents with the word processor installed (Microsoft Word, WPS Office) — it lays them out as their
    /// authors saw them — and with LibreOffice when there is none or it fails; other documents with LibreOffice.
    /// </summary>
    Automatic,

    /// <summary>The word processor installed (Word documents only).</summary>
    WordProcessor,

    /// <summary>LibreOffice.</summary>
    LibreOffice,
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
/// <param name="Converter">Which program converts.</param>
public sealed record OfficeToPdfParams(
    [property: Range(10, 3600)] int TimeoutSeconds = 180,
    string? LibreOfficePath = null,
    bool Overwrite = false,
    OfficeConverter Converter = OfficeConverter.Automatic);

/// <summary>A converted document.</summary>
/// <param name="Output">The PDF written.</param>
/// <param name="PageCount">Its pages.</param>
/// <param name="ConvertedBy">The program that converted it (<see cref="OfficeConverter.WordProcessor"/> or <see cref="OfficeConverter.LibreOffice"/>).</param>
public sealed record OfficeConversionResult(string Output, int PageCount, OfficeConverter ConvertedBy = OfficeConverter.LibreOffice);

/// <summary>
/// convert.officeToPdf (<see cref="OperationKind.Export"/>, "Office sang PDF"): Word, Excel, PowerPoint, OpenDocument
/// and RTF documents converted to PDF. Word documents go to the word processor installed (Microsoft Word, or WPS
/// Office, through Word's automation — <see cref="IWordConverter"/>), which lays them out exactly as their authors saw
/// them, and to LibreOffice when there is none or it fails (a document it says is protected is not tried again);
/// spreadsheets and presentations go to LibreOffice. LibreOffice runs as a separate process (<c>soffice --headless --convert-to pdf</c>,
/// through <see cref="IExternalToolRunner"/>: no shell, a timeout, cancellable). Every run has a user profile of its own
/// (<c>-env:UserInstallation</c>), so that several conversions run side by side and never touch the user's own
/// LibreOffice settings — in a short folder of the system's temporary folder: LibreOffice's profile nests deep, and
/// past about 150 characters of profile path soffice crashes (0xC0000409) on Windows. The document is copied under a
/// plain name into a working folder next to the output; both folders are removed afterwards. The PDF must read back
/// with its pages, and is moved into place only then.
/// </summary>
/// <param name="runner">Runs LibreOffice.</param>
/// <param name="locator">Finds LibreOffice.</param>
/// <param name="word">The word processor's conversion; null to convert with LibreOffice only.</param>
public sealed class OfficeToPdfOperation(IExternalToolRunner runner, ILibreOfficeLocator locator, IWordConverter? word = null) : PdfOperation<OfficeToPdfParams>
{
    public const string OperationId = "convert.officeToPdf";

    public const string Stage = "Stage_OfficeToPdf";

    /// <summary>The document types converted (LibreOffice reads more, but these are the ones tested).</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".odt", ".rtf", ".xlsx", ".xls", ".ods", ".pptx", ".ppt", ".odp",
    };

    /// <summary>The word-processing documents, which the word processor converts when there is one.</summary>
    public static IReadOnlySet<string> WordExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".odt", ".rtf",
    };

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Whether <paramref name="path"/> is a document this operation converts.</summary>
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Whether <paramref name="path"/> is a word-processing document (Word, OpenDocument text, RTF).</summary>
    public static bool IsWordDocument(string path) => WordExtensions.Contains(Path.GetExtension(path));

    /// <summary>Converts <paramref name="input"/> into the PDF <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="LibreOfficeNotFoundException">
    /// LibreOffice is needed (chosen, or the document is not a Word document, or no word processor is installed) and
    /// is not installed (or not where it was said to be).
    /// </exception>
    /// <exception cref="WordProcessorNotFoundException">The word processor was chosen and none is installed.</exception>
    /// <exception cref="OfficeConversionException">The document is not supported, or the program failed on it.</exception>
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

        var (processor, soffice) = Converters(input, parameters);
        return OptimizedFiles.WriteAsync(
            output,
            async (temporary, token) =>
            {
                if (processor is not null)
                {
                    try
                    {
                        var pages = await ConvertIntoAsync(input, temporary, (source, pdf, t) => word!.ConvertAsync(processor, source, pdf, TimeSpan.FromSeconds(parameters.TimeoutSeconds), t), token).ConfigureAwait(false);
                        return new OfficeConversionResult(output, pages, OfficeConverter.WordProcessor);
                    }
                    catch (OfficeConversionException ex) when (ex.Failure == OfficeConversionFailure.Failed && soffice is not null)
                    {
                        // The word processor could not: LibreOffice may.
                    }
                }

                var converted = await ConvertIntoAsync(input, temporary, (source, pdf, t) => RunLibreOfficeAsync(soffice!, source, pdf, parameters, t), token).ConfigureAwait(false);
                return new OfficeConversionResult(output, converted, OfficeConverter.LibreOffice);
            },
            cancellationToken);
    }

    /// <summary>
    /// What converts <paramref name="input"/>: the word processor (a Word document, when one is installed and
    /// LibreOffice was not chosen) and LibreOffice (when it is installed and may be needed: for another document,
    /// when it was chosen, or to try after the word processor). At least one of them, or the exception saying what is
    /// missing.
    /// </summary>
    private (WordProcessor? Processor, string? Soffice) Converters(string input, OfficeToPdfParams parameters)
    {
        var wordDocument = IsWordDocument(input);
        switch (parameters.Converter)
        {
            case OfficeConverter.WordProcessor:
                if (!wordDocument)
                {
                    throw new OfficeConversionException(OfficeConversionFailure.Unsupported);
                }

                return (word?.Find() ?? throw new WordProcessorNotFoundException(), null);

            case OfficeConverter.LibreOffice:
                return (null, locator.Find(parameters.LibreOfficePath) ?? throw new LibreOfficeNotFoundException());

            default:
                var processor = wordDocument ? word?.Find() : null;
                var soffice = locator.Find(parameters.LibreOfficePath);
                return processor is null && soffice is null ? throw new LibreOfficeNotFoundException() : (processor, soffice);
        }
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

    /// <summary>
    /// Converts a copy of the document, under a plain name in a working folder of its own (the converting program
    /// never sees the document's own path, nor locks it), with <paramref name="convert"/> (copy, PDF to write); the
    /// PDF must read back with its pages and is then moved to <paramref name="output"/>. The page count.
    /// </summary>
    private static async Task<int> ConvertIntoAsync(string input, string output, Func<string, string, CancellationToken, Task> convert, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetDirectoryName(output)!, $".pdfpro-office-{Guid.NewGuid():N}");
        var source = Path.Combine(work, "in", "document" + Path.GetExtension(input).ToLowerInvariant());
        var converted = Path.Combine(work, "out", "document.pdf");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(converted)!);
            File.Copy(input, source);
            await convert(source, converted, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(converted))
            {
                throw new OfficeConversionException(OfficeConversionFailure.Failed);
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

    /// <summary>Runs LibreOffice on <paramref name="source"/>; its PDF, <paramref name="converted"/>, is written next to it in "out".</summary>
    private async Task RunLibreOfficeAsync(string soffice, string source, string converted, OfficeToPdfParams parameters, CancellationToken cancellationToken)
    {
        var work = Path.GetDirectoryName(Path.GetDirectoryName(source))!;
        var profileFolder = ProfileFolder();
        try
        {
            var profile = new Uri(profileFolder + Path.DirectorySeparatorChar).AbsoluteUri;
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

            if (!result.Succeeded)
            {
                throw new OfficeConversionException(OfficeConversionFailure.Failed, new ExternalToolFailedException(result.ToolName, result.ExitCode, ExternalToolResult.Tail(result.StandardError)));
            }
        }
        finally
        {
            Remove(profileFolder);
        }
    }

    /// <summary>
    /// A new folder for a conversion's LibreOffice profile, its path short wherever the output goes: soffice crashes on
    /// Windows when its profile path is longer than about 150 characters (its own folders nest deep inside).
    /// </summary>
    internal static string ProfileFolder() => Path.Combine(Path.GetTempPath(), "pdfpro-lo-" + Guid.NewGuid().ToString("N")[..12]);

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

    /// <summary>The pages of what was written; a file that is not a readable PDF with pages is a failure.</summary>
    private static int PageCount(string path)
    {
        try
        {
            using var document = PdfSafeReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount > 0 ? document.PageCount : throw new OfficeConversionException(OfficeConversionFailure.Failed);
        }
        catch (Exception ex) when (ex is not OfficeConversionException and not OutOfMemoryException and not IOException)
        {
            throw new OfficeConversionException(OfficeConversionFailure.Failed, ex);
        }
    }

    /// <summary>The working folder goes; the converting program may still hold one of its files for a moment.</summary>
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
