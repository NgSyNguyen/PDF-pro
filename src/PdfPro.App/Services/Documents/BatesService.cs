using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Operations;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>A file to number: where it is, the name the list shows and its pages.</summary>
public sealed record BatesFile(string Path, string FileName, int PageCount);

/// <summary>Reads the files of a Bates run and writes their numbered copies (seam for view-model tests).</summary>
public interface IBatesService
{
    /// <summary>
    /// Reads a file to number. Returns null when it cannot be numbered (damaged, unreadable or password protected);
    /// the user has been told why.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="fileName">What the list shows, or null for the file's name.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    Task<BatesFile?> AddAsync(string path, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>Numbers the files in order and writes the copies into <paramref name="outputFolder"/> (all or nothing).</summary>
    /// <exception cref="BatesFileException">A file could not be read or written.</exception>
    Task<BatesResult> WriteAsync(IReadOnlyList<string> inputs, BatesParams parameters, string outputFolder, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Bates numbering as the app runs it: files are read with PDFium when added (protected files are refused until
/// encryption is supported), then numbered by <see cref="BatesOperation"/>. Logs never name files.
/// </summary>
internal sealed class BatesService(
    BatesOperation operation,
    IPdfDocumentLoader loader,
    IMessageService messages,
    ILogger<BatesService> logger) : IBatesService
{
    public async Task<BatesFile?> AddAsync(string path, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = fileName ?? Path.GetFileName(path);
        string? problem;
        try
        {
            var document = await loader.LoadAsync(path, null, cancellationToken);
            await using (document.ConfigureAwait(true))
            {
                if (!document.Features.IsEncrypted)
                {
                    return new BatesFile(Path.GetFullPath(path), name, document.PageCount);
                }
            }

            logger.LogInformation("A protected file was not added to a Bates run");
            problem = Strings.Bates_Error_Encrypted;
        }
        catch (PdfDocumentException ex) when (ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
        {
            logger.LogInformation("A protected file was not added to a Bates run");
            problem = Strings.Bates_Error_Encrypted;
        }
        catch (PdfDocumentException ex)
        {
            logger.LogWarning("A file could not be added to a Bates run: {Failure}", ex.Failure);
            problem = DocumentOpener.MessageFor(ex.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A file could not be read for a Bates run: {ErrorType}", ex.GetType().Name);
            problem = Strings.Open_Error_ReadFailed;
        }

        await messages.ShowErrorAsync(Strings.Bates_Error_AddTitle, string.Format(CultureInfo.CurrentCulture, problem, name));
        return null;
    }

    public Task<BatesResult> WriteAsync(IReadOnlyList<string> inputs, BatesParams parameters, string outputFolder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => operation.WriteAsync(inputs, parameters, outputFolder, progress, cancellationToken);
}
