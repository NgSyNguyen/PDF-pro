using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Merging;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure;
using PdfPro.Operations.Merging;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>Reads the files of a merge and writes the merged file (seam for view-model tests).</summary>
public interface IMergeService
{
    /// <summary>
    /// Reads a file to merge, asking for its password when it has one. Returns null when the user cancelled or the
    /// file cannot be merged (the user has been told why).
    /// </summary>
    Task<MergeSource?> AddAsync(string path, string? displayName = null, CancellationToken cancellationToken = default);

    /// <summary>The merged document's page order, for the preview.</summary>
    /// <exception cref="OperationValidationException">The items do not fit the files.</exception>
    MergeLayout Layout(IReadOnlyList<MergeSource> sources, MergeParams parameters);

    /// <summary>Writes the merged file (through a temporary file); returns its path.</summary>
    Task<string> WriteAsync(IReadOnlyList<MergeSource> sources, MergeParams parameters, string outputPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The merge as the app runs it: files are read with PDFium when added (protected files ask for their password,
/// which stays in memory), then written by <see cref="MergeOperation"/>. Logs never name files.
/// </summary>
internal sealed class MergeService(
    MergeOperation operation,
    IPdfDocumentLoader loader,
    IPasswordPrompt passwords,
    IMessageService messages,
    AppPaths paths,
    ILogger<MergeService> logger) : IMergeService
{
    public async Task<MergeSource?> AddAsync(string path, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileName = displayName ?? Path.GetFileName(path);
        string? password = null;
        while (true)
        {
            try
            {
                return await MergeSource.OpenAsync(loader, path, password, cancellationToken);
            }
            catch (PdfDocumentException ex) when (ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
            {
                password = passwords.RequestPassword(fileName, isRetry: ex.Failure == PdfOpenFailure.WrongPassword);
                if (password is null)
                {
                    return null;
                }
            }
            catch (PdfDocumentException ex)
            {
                logger.LogWarning("A file could not be added to a merge: {Failure}", ex.Failure);
                await messages.ShowErrorAsync(Strings.Merge_Error_AddTitle, string.Format(CultureInfo.CurrentCulture, DocumentOpener.MessageFor(ex.Failure), fileName));
                return null;
            }
            catch (MergeSourceException ex)
            {
                logger.LogWarning("A protected file could not be added to a merge: {Failure}", ex.Failure);
                await messages.ShowErrorAsync(Strings.Merge_Error_AddTitle, string.Format(CultureInfo.CurrentCulture, Strings.Merge_Error_NotPermitted, fileName));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("A file could not be read for a merge: {ErrorType}", ex.GetType().Name);
                await messages.ShowErrorAsync(Strings.Merge_Error_AddTitle, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_ReadFailed, fileName));
                return null;
            }
        }
    }

    public MergeLayout Layout(IReadOnlyList<MergeSource> sources, MergeParams parameters) => operation.Layout(sources, parameters);

    public Task<string> WriteAsync(IReadOnlyList<MergeSource> sources, MergeParams parameters, string outputPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => operation.WriteAsync(sources, parameters, outputPath, Path.Combine(paths.CacheDirectory, "merge"), progress, cancellationToken);
}
