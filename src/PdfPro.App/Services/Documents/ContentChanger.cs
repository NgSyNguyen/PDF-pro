using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>Why a content change failed; the document is unchanged in every case.</summary>
public enum ContentChangeFailure
{
    /// <summary>A source file could not be read to write the pages.</summary>
    SourceUnreadable,

    DiskFull,

    Failed,
}

/// <summary>A content change failed; <see cref="Failure"/> says why. The message never contains paths.</summary>
public sealed class ContentChangeException(ContentChangeFailure failure, Exception? innerException = null)
    : Exception($"The content change failed: {failure}.", innerException)
{
    public ContentChangeFailure Failure { get; } = failure;
}

/// <summary>Applies content operations to open documents (seam for view-model tests).</summary>
public interface IContentChanger
{
    /// <summary>
    /// Applies a content operation (page size, later watermarks, OCR…) to the document as one undoable step: the
    /// document as its plan shows it is written to the session's next revision file with the change, which then
    /// becomes the document's base. Edits are blocked meanwhile.
    /// </summary>
    /// <exception cref="OperationValidationException">The parameters do not fit the document.</exception>
    /// <exception cref="ContentChangeException">The change failed; nothing changed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing changed.</exception>
    Task ApplyAsync(DocumentWorkspace workspace, IContentOperation operation, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Content change in a session (SPEC "DocumentSession và Revision"): materialize the current plan into the work
/// folder, let the operation rewrite it into revisions/rNNNN.pdf (written to a temporary name, flushed, then renamed),
/// open the revision and record a <see cref="Core.Sessions.RevisionCommand"/>. Undo goes back to the previous
/// state; the revision file stays for redo and crash recovery. Logs name the session and operation only.
/// </summary>
internal sealed class ContentChanger(IPlanMaterializer materializer, IPdfDocumentLoader loader, ILogger<ContentChanger> logger) : IContentChanger
{
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorDiskFull = unchecked((int)0x80070070);

    public async Task ApplyAsync(DocumentWorkspace workspace, IContentOperation operation, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(progress);
        if (!workspace.CanEdit)
        {
            throw new InvalidOperationException("The document cannot be edited right now.");
        }

        var before = workspace.Session.State;
        var validation = operation.Validate(parameters, new DocumentInfo(before.Plan.Count));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(operation.Id, validation.Errors);
        }

        var revision = workspace.ReserveRevision();
        var pages = Path.Combine(workspace.WorkDirectory, $"{Guid.NewGuid():N}.pdf");
        var temporary = revision.Path + ".tmp";
        IPdfDocument? document = null;
        workspace.IsBusy = true;
        try
        {
            Directory.CreateDirectory(workspace.WorkDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(revision.Path)!);
            var request = new MaterializeRequest
            {
                Plan = before.Plan,
                BaseSourceId = before.BaseSourceId,
                SourcePaths = workspace.SourcePaths,
                WorkingDirectory = workspace.WorkDirectory,
            };

            await using (var stream = new FileStream(pages, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await materializer.MaterializeAsync(request, stream, Part(progress, 0), cancellationToken);
            }

            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await operation.ApplyToFileAsync(pages, stream, parameters, Part(progress, 1), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, revision.Path, overwrite: true);
            document = await loader.LoadAsync(revision.Path, password: null, cancellationToken);
            if (document.PageCount != before.Plan.Count)
            {
                throw new InvalidDataException($"The revision has {document.PageCount} pages instead of {before.Plan.Count}.");
            }
        }
        catch (Exception ex)
        {
            if (document is not null)
            {
                await document.DisposeAsync();
            }

            TryDelete(temporary);
            TryDelete(revision.Path);
            if (ex is OperationValidationException)
            {
                throw;
            }

            if (ex is OperationCanceledException)
            {
                logger.LogInformation("Session {SessionId}: {OperationId} cancelled", workspace.Session.Id, operation.Id);
                throw;
            }

            var failure = Classify(ex);
            logger.LogWarning("Session {SessionId}: {OperationId} failed: {Failure} ({ErrorType})", workspace.Session.Id, operation.Id, failure, ex.GetType().Name);
            throw new ContentChangeException(failure, ex);
        }
        finally
        {
            TryDelete(pages);
            workspace.IsBusy = false;
        }

        try
        {
            workspace.CompleteRevision(before, operation.Id, parameters, revision, document);
        }
        catch
        {
            await document.DisposeAsync();
            TryDelete(revision.Path);
            throw;
        }
    }

    private static ContentChangeFailure Classify(Exception error) => error switch
    {
        MaterializationException => ContentChangeFailure.SourceUnreadable,
        IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } => ContentChangeFailure.DiskFull,
        _ => ContentChangeFailure.Failed,
    };

    /// <summary>Progress of one half of the work (writing the pages, then changing them) as progress of the whole.</summary>
    private static IProgress<OperationProgress> Part(IProgress<OperationProgress> progress, int half) => new PartProgress(progress, half);

    /// <summary>Forwards at once (no posting), so reports keep their order.</summary>
    private sealed class PartProgress(IProgress<OperationProgress> whole, int half) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
            => whole.Report(new OperationProgress((half * 1000) + (int)(value.Fraction * 1000), 2000, value.StageKey));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("A temporary file of a content change could not be removed: {ErrorType}", ex.GetType().Name);
        }
    }
}
