using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>Why a save failed; the original file is unchanged in every case.</summary>
public enum SaveFailure
{
    /// <summary>Another program holds the file open.</summary>
    Locked,

    /// <summary>The file is read-only or the folder is protected.</summary>
    AccessDenied,

    DiskFull,

    /// <summary>A source file could not be read to build the new file.</summary>
    SourceUnreadable,

    /// <summary>The written file could not be opened again (it was not used).</summary>
    VerificationFailed,

    Failed,
}

/// <summary>A save failed; <see cref="Failure"/> says why. The message never contains paths.</summary>
public sealed class DocumentSaveException(SaveFailure failure, Exception? innerException = null)
    : Exception($"The document could not be saved: {failure}.", innerException)
{
    public SaveFailure Failure { get; } = failure;
}

/// <summary>Writes documents to disk (seam for view-model tests).</summary>
public interface IDocumentSaver
{
    /// <summary>
    /// Writes the document as its page plan shows it to <paramref name="targetPath"/> (its own file, or another one
    /// for "Save as"), then marks it saved.
    /// </summary>
    /// <exception cref="DocumentSaveException">The save failed; nothing was replaced.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was replaced.</exception>
    Task SaveAsync(DocumentWorkspace workspace, string targetPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Safe save (SPEC "Lưu an toàn", AGENTS rule 4): the target, temporary and backup paths are recorded in the session
/// manifest first; the plan is then materialized into the temporary file next to the target, flushed, opened again
/// with PDFium to check it, and swapped in with File.Replace and a temporary backup. A crash at any point leaves either
/// the old or the new file (the next start removes a partial temporary file or puts the old file back), and the session
/// still holds the changes. Edits are blocked while saving so the saved state is exactly the save point.
/// </summary>
internal sealed class DocumentSaver(IPlanMaterializer materializer, IPdfDocumentLoader loader, ILogger<DocumentSaver> logger) : IDocumentSaver
{
    public async Task SaveAsync(DocumentWorkspace workspace, string targetPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(progress);
        if (workspace.IsReadOnly)
        {
            throw new InvalidOperationException("Password protected documents cannot be saved yet.");
        }

        var path = Path.GetFullPath(targetPath);
        var request = new MaterializeRequest
        {
            Plan = workspace.Session.Plan,
            BaseSourceId = workspace.Session.BaseSourceId,
            SourcePaths = workspace.SourcePaths,
            WorkingDirectory = workspace.WorkDirectory,
        };
        var backup = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");

        workspace.IsBusy = true;
        try
        {
            await AtomicFile.WriteAsync(
                path,
                (stream, token) => materializer.MaterializeAsync(request, stream, progress, token),
                new AtomicWriteOptions
                {
                    BackupPath = backup,
                    DeleteBackupAfterSwap = true,
                    VerifyAsync = (temp, token) => VerifyAsync(temp, request.Plan.Count, token),
                    BeforeWriteAsync = (temp, backupPath, _) => workspace.RecordPendingSaveAsync(new PendingSaveDto(path, temp, backupPath)),
                },
                cancellationToken);

            workspace.CompleteSave(path, FileStamp.Read(path));
            logger.LogInformation("Session {SessionId} saved: {PageCount} pages", workspace.Session.Id, request.Plan.Count);
        }
        catch (MaterializationException ex)
        {
            logger.LogWarning("Saving session {SessionId} failed: {Failure} (source {SourceId})", workspace.Session.Id, ex.Failure, ex.SourceId);
            throw new DocumentSaveException(SaveFailure.SourceUnreadable, ex);
        }
        catch (FileSaveException ex)
        {
            // The exception's own text may contain the path: log the classification only.
            logger.LogWarning("Saving session {SessionId} failed: {Failure} (HRESULT 0x{HResult:X8})", workspace.Session.Id, ex.Failure, ex.HResult);
            throw new DocumentSaveException(ex.Failure switch
            {
                FileSaveFailure.Locked => SaveFailure.Locked,
                FileSaveFailure.AccessDenied => SaveFailure.AccessDenied,
                FileSaveFailure.DiskFull => SaveFailure.DiskFull,
                _ => SaveFailure.Failed,
            }, ex);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError("Saving session {SessionId} failed: the written file did not open again", workspace.Session.Id);
            throw new DocumentSaveException(SaveFailure.VerificationFailed, ex);
        }
        finally
        {
            await workspace.ClearPendingSaveAsync();
            workspace.IsBusy = false;
        }
    }

    /// <summary>The finished temporary file must open and have every page, or it does not replace anything.</summary>
    private async Task VerifyAsync(string path, int expectedPages, CancellationToken cancellationToken)
    {
        IPdfDocument document;
        try
        {
            document = await loader.LoadAsync(path, password: null, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfDocumentException ex)
        {
            throw new InvalidDataException("The written file cannot be opened.", ex);
        }

        await using (document.ConfigureAwait(false))
        {
            if (document.PageCount != expectedPages)
            {
                throw new InvalidDataException($"The written file has {document.PageCount} pages instead of {expectedPages}.");
            }
        }
    }
}
