using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Security;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Signing;
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

    /// <summary>
    /// "Trích trang": writes pages of the document (as the plan shows them) to a new file; the document itself and its
    /// saved state do not change.
    /// </summary>
    /// <param name="workspace">The document.</param>
    /// <param name="pageIndices">The pages to write (0-based, in any order: they keep the document's order).</param>
    /// <param name="targetPath">The new file (never the document's own file).</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Cancels; nothing is written then.</param>
    /// <exception cref="DocumentSaveException">Writing failed; an existing file at the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task ExportAsync(DocumentWorkspace workspace, IReadOnlyList<int> pageIndices, string targetPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Saving keeps the document's digital signatures valid: its only changes are comments added in the session, which
    /// are appended to the file as an incremental update instead of writing it anew.
    /// </summary>
    bool KeepsSignatures(DocumentWorkspace workspace);
}

/// <summary>
/// Safe save (SPEC "Lưu an toàn", AGENTS rule 4): the target, temporary and backup paths are recorded in the session
/// manifest first; the plan is then materialized into the temporary file next to the target, flushed, opened again
/// with PDFium to check it, and swapped in with File.Replace and a temporary backup. A crash at any point leaves either
/// the old or the new file (the next start removes a partial temporary file or puts the old file back), and the session
/// still holds the changes. Edits are blocked while saving so the saved state is exactly the save point.
/// </summary>
internal sealed class DocumentSaver(
    IPlanMaterializer materializer,
    IPdfDocumentLoader loader,
    ExtractPagesOperation extract,
    ILogger<DocumentSaver> logger) : IDocumentSaver
{
    public bool KeepsSignatures(DocumentWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var session = workspace.Session;
        return workspace.Protection is null
            && session.BaseSourceId == SourceIds.Original
            && workspace.BaseDocument.Features.HasDigitalSignatures
            && session.Plan.AddsOnlyAnnotations(session.BaseSourceId, workspace.BaseDocument.PageCount);
    }

    public async Task SaveAsync(DocumentWorkspace workspace, string targetPath, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(progress);
        if (workspace.IsReadOnly)
        {
            throw new InvalidOperationException("A view-only protected document cannot be saved: it needs its owner password.");
        }

        // Never saved unprotected by mistake: a protection without its passwords stops the save.
        var security = workspace.Protection is { } protection
            ? workspace.Passwords.SecurityOf(protection) ?? throw new InvalidOperationException("The passwords of the document's protection are needed to save it.")
            : null;
        var path = Path.GetFullPath(targetPath);
        var request = new MaterializeRequest
        {
            Plan = workspace.Session.Plan,
            BaseSourceId = workspace.Session.BaseSourceId,
            SourcePaths = workspace.SourcePaths,
            WorkingDirectory = workspace.WorkDirectory,
            Security = security,
        };

        workspace.IsBusy = true;
        try
        {
            var update = KeepsSignatures(workspace) ? await IncrementalAsync(workspace, request, cancellationToken) : null;
            await WriteAsync(workspace, request, path, progress, cancellationToken, update);
            workspace.CompleteSave(path, FileStamp.Read(path));
            logger.LogInformation("Session {SessionId} saved: {PageCount} pages", workspace.Session.Id, request.Plan.Count);
        }
        finally
        {
            workspace.IsBusy = false;
        }
    }

    public async Task ExportAsync(
        DocumentWorkspace workspace,
        IReadOnlyList<int> pageIndices,
        string targetPath,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(pageIndices);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(progress);
        if (workspace.IsReadOnly)
        {
            throw new InvalidOperationException("Pages of password protected documents cannot be extracted yet.");
        }

        var path = Path.GetFullPath(targetPath);
        if (workspace.FilePath is { } own && string.Equals(Path.GetFullPath(own), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Extracted pages cannot replace the document's own file.", nameof(targetPath));
        }

        // The plan and the source files are immutable: editing may go on while the pages are written.
        var request = new MaterializeRequest
        {
            Plan = extract.SelectPages(workspace.Session.Plan, new ExtractPagesParams(PageRanges.Format(pageIndices))),
            BaseSourceId = workspace.Session.BaseSourceId,
            SourcePaths = workspace.SourcePaths,
            WorkingDirectory = workspace.WorkDirectory,
        };

        await WriteAsync(workspace, request, path, progress, cancellationToken);
        logger.LogInformation("Session {SessionId}: {PageCount} pages extracted", workspace.Session.Id, request.Plan.Count);
    }

    /// <summary>
    /// The document's file with the session's comments appended as an incremental update, or null when that cannot be
    /// done (then it is written anew, as any save).
    /// </summary>
    private async Task<byte[]?> IncrementalAsync(DocumentWorkspace workspace, MaterializeRequest request, CancellationToken cancellationToken)
    {
        var pages = request.Plan.Select((page, index) => (index, page.Annotations)).Where(p => p.Annotations.Count > 0).ToList();
        try
        {
            var original = await File.ReadAllBytesAsync(request.SourcePaths[request.BaseSourceId], cancellationToken);
            return await Task.Run(() => IncrementalAnnotations.Append(original, pages), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning("Session {SessionId}: the comments cannot be appended to the signed file ({ErrorType}); it is written anew", workspace.Session.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Materializes <paramref name="request"/> into <paramref name="path"/> safely — or writes <paramref name="update"/>,
    /// the file already updated — the manifest recording the files while they are written.
    /// </summary>
    private async Task WriteAsync(
        DocumentWorkspace workspace,
        MaterializeRequest request,
        string path,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken,
        byte[]? update = null)
    {
        var backup = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");
        try
        {
            await AtomicFile.WriteAsync(
                path,
                (stream, token) => update is null ? materializer.MaterializeAsync(request, stream, progress, token) : stream.WriteAsync(update, token).AsTask(),
                new AtomicWriteOptions
                {
                    BackupPath = backup,
                    DeleteBackupAfterSwap = true,
                    VerifyAsync = (temp, token) => VerifyAsync(temp, request.Plan.Count, request.Security, token),
                    BeforeWriteAsync = (temp, backupPath, _) => workspace.RecordPendingSaveAsync(new PendingSaveDto(path, temp, backupPath)),
                },
                cancellationToken);
        }
        catch (MaterializationException ex)
        {
            logger.LogWarning("Writing session {SessionId} failed: {Failure} (source {SourceId})", workspace.Session.Id, ex.Failure, ex.SourceId);
            throw new DocumentSaveException(SaveFailure.SourceUnreadable, ex);
        }
        catch (FileSaveException ex)
        {
            // The exception's own text may contain the path: log the classification only.
            logger.LogWarning("Writing session {SessionId} failed: {Failure} (HRESULT 0x{HResult:X8})", workspace.Session.Id, ex.Failure, ex.HResult);
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
            logger.LogError("Writing session {SessionId} failed: the written file did not open again", workspace.Session.Id);
            throw new DocumentSaveException(SaveFailure.VerificationFailed, ex);
        }
        finally
        {
            await workspace.ClearPendingSaveAsync();
        }
    }

    /// <summary>
    /// The finished temporary file must open (with its open password when protected) and have every page — and be
    /// protected when it should be — or it does not replace anything.
    /// </summary>
    private async Task VerifyAsync(string path, int expectedPages, DocumentSecurity? security, CancellationToken cancellationToken)
    {
        IPdfDocument document;
        try
        {
            document = await loader.LoadAsync(path, security?.UserPassword, cancellationToken).ConfigureAwait(false);
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

            if (security is not null && !document.Features.IsEncrypted)
            {
                throw new InvalidDataException("The written file is not protected.");
            }
        }
    }
}
