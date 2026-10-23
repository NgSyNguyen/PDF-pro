using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>Opens documents into sessions (seam for view-model tests).</summary>
public interface IDocumentWorkspaceService
{
    /// <summary>
    /// Copies the file into a new session folder and opens the copy, asking for a password if needed. Returns null when
    /// the user cancelled or the file could not be opened (the user has already been told why).
    /// </summary>
    Task<DocumentWorkspace?> OpenAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Open flow (SPEC "DocumentSession và Revision"): copy the file into %LOCALAPPDATA%\PdfPro\sessions\{id}\sources\s0.pdf,
/// open the copy with PDFium (the original is never held open or written), then start a session on an identity plan
/// carrying the first-page marks remembered for the unchanged file. Logs name the session by id only.
/// </summary>
internal sealed class DocumentWorkspaceService(
    SessionStore store,
    IDocumentOpener opener,
    ISplitMarkMemory splitMarks,
    IMessageService messages,
    ILoggerFactory loggers) : IDocumentWorkspaceService
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private readonly ILogger _logger = loggers.CreateLogger<DocumentWorkspaceService>();

    public async Task<DocumentWorkspace?> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        var stamp = FileStamp.Read(fullPath);

        SessionFolder folder;
        try
        {
            folder = await store.CreateAsync(fullPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failure = CopyFailure(ex);
            _logger.LogWarning("Copying a document into a new session failed: {Failure} ({ErrorType})", failure, ex.GetType().Name);
            await messages.ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, DocumentOpener.MessageFor(failure), fileName));
            return null;
        }

        try
        {
            var copy = folder.PathOf(SessionStore.SourceFileName(SourceIds.Original));
            var document = await opener.OpenAsync(copy, fileName, cancellationToken);
            if (document is null)
            {
                store.Delete(folder);
                return null;
            }

            if (document.PageCount == 0)
            {
                await document.DisposeAsync();
                store.Delete(folder);
                await messages.ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_Corrupted, fileName));
                return null;
            }

            var source = new SessionSource(SourceIds.Original, SessionStore.SourceFileName(SourceIds.Original), SessionSourceKind.Original, document.PageCount, fileName);
            var remembered = stamp is { } known && !document.Features.IsEncrypted
                ? await splitMarks.RecallAsync(fullPath, known, document.PageCount, cancellationToken)
                : [];
            var plan = PagePlan.Identity(SourceIds.Original, document.PageCount, RandomPageKeys.Instance)
                .WithSplitMarks(remembered.ToDictionary(m => m.Page, m => new SplitMark(m.FileName)));
            var session = new DocumentSession(folder.Id, folder.Directory, fullPath, [source], new SessionState(plan, SourceIds.Original, 0));
            return new DocumentWorkspace(
                session,
                store,
                folder,
                [new OpenSource(source, copy, document)],
                stamp,
                DateTimeOffset.UtcNow,
                loggers.CreateLogger<DocumentWorkspace>(),
                splitMarks);
        }
        catch
        {
            store.Delete(folder);
            throw;
        }
    }

    private static PdfOpenFailure CopyFailure(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => PdfOpenFailure.FileNotFound,
        UnauthorizedAccessException => PdfOpenFailure.AccessDenied,
        IOException { HResult: ErrorSharingViolation or ErrorLockViolation } => PdfOpenFailure.FileInUse,
        _ => PdfOpenFailure.ReadFailed,
    };
}
