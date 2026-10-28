using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Security;
using PdfPro.Core.Organizing;
using PdfPro.Core.Security;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Splitting;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>Why a protected document could not be opened for editing with the password given.</summary>
public enum UnlockFailure
{
    /// <summary>The password opens nothing.</summary>
    WrongPassword,

    /// <summary>It is the open password, not the owner password.</summary>
    NotOwnerPassword,

    /// <summary>The file could not be read again (moved, locked, damaged).</summary>
    Unreadable,
}

/// <summary>
/// What a protected document needs to be opened for editing: its owner password, and its open password when the user
/// typed it before (so the protection can be kept on save without asking). Never shows the passwords.
/// </summary>
/// <param name="OwnerPassword">The owner password.</param>
/// <param name="OpenPassword">The open password typed earlier, or null when unknown or there is none.</param>
/// <param name="HasOpenPassword">Opening the file takes a password.</param>
public sealed record ProtectionUnlock(string OwnerPassword, string? OpenPassword, bool HasOpenPassword)
{
    public override string ToString() => $"ProtectionUnlock {{ open password: {(HasOpenPassword ? "yes" : "no")} }}";
}

/// <summary>The document opened for editing, or why not.</summary>
public sealed record UnlockResult(DocumentWorkspace? Workspace, UnlockFailure? Failure);

/// <summary>Opens documents into sessions (seam for view-model tests).</summary>
public interface IDocumentWorkspaceService
{
    /// <summary>
    /// Copies the file into a new session folder and opens the copy, asking for a password if needed. Returns null when
    /// the user cancelled or the file could not be opened (the user has already been told why).
    /// </summary>
    Task<DocumentWorkspace?> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a protected file again, editable, with its owner password (the caller replaces the view-only tab). Nothing
    /// is asked and no message is shown: a failure says why.
    /// </summary>
    Task<UnlockResult> OpenUnlockedAsync(string path, ProtectionUnlock unlock, CancellationToken cancellationToken = default);
}

/// <summary>
/// Open flow (SPEC "DocumentSession và Revision"): copy the file into %LOCALAPPDATA%\PdfPro\sessions\{id}\sources\s0.pdf,
/// open the copy with PDFium (the original is never held open or written), then start a session on an identity plan
/// carrying the first-page marks remembered for the unchanged file. Logs name the session by id only.
/// A protected file opened with its owner password (SPEC P8) is editable: its session copy is written again without
/// encryption (the session folder is the user's own and goes when the document is closed), the plan names the
/// protection it is saved with and the passwords stay in the workspace's memory. With the open password only, it is
/// view-only until the owner password is given (<see cref="OpenUnlockedAsync"/>).
/// </summary>
internal sealed class DocumentWorkspaceService(
    SessionStore store,
    IDocumentOpener opener,
    IPdfDocumentLoader loader,
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
        var (folder, copyFailure) = await CreateFolderAsync(fullPath, cancellationToken);
        if (folder is null)
        {
            await messages.ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, DocumentOpener.MessageFor(copyFailure), fileName));
            return null;
        }

        try
        {
            var copy = folder.PathOf(SessionStore.SourceFileName(SourceIds.Original));
            if (await opener.OpenAsync(copy, fileName, cancellationToken: cancellationToken) is not { } opened)
            {
                store.Delete(folder);
                return null;
            }

            var document = opened.Document;
            if (document.PageCount == 0)
            {
                await document.DisposeAsync();
                store.Delete(folder);
                await messages.ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_Corrupted, fileName));
                return null;
            }

            // Typed at the prompt, the owner password unlocks it (PDFsharp confirms it is the owner's); its open password
            // (needed, as it was asked for) is unknown.
            if (document.Features.HasEveryPermission && opened.Password is { } owner)
            {
                var unlocked = await UnlockAsync(document, copy, new ProtectionUnlock(owner, OpenPassword: null, HasOpenPassword: true), cancellationToken);
                if (unlocked.Document is { } plain)
                {
                    return await StartAsync(folder, fullPath, fileName, stamp, copy, plain, unlocked.Vault, unlocked.Protection, openPassword: null, cancellationToken);
                }

                document = await loader.LoadAsync(copy, owner, cancellationToken);
            }

            return await StartAsync(folder, fullPath, fileName, stamp, copy, document, new ProtectionPasswordVault(), protection: null, opened.Password, cancellationToken);
        }
        catch
        {
            store.Delete(folder);
            throw;
        }
    }

    public async Task<UnlockResult> OpenUnlockedAsync(string path, ProtectionUnlock unlock, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(unlock);
        var fullPath = Path.GetFullPath(path);
        var stamp = FileStamp.Read(fullPath);
        var (folder, _) = await CreateFolderAsync(fullPath, cancellationToken);
        if (folder is null)
        {
            return new UnlockResult(null, UnlockFailure.Unreadable);
        }

        try
        {
            var copy = folder.PathOf(SessionStore.SourceFileName(SourceIds.Original));
            IPdfDocument document;
            try
            {
                document = await loader.LoadAsync(copy, unlock.OwnerPassword, cancellationToken);
            }
            catch (PdfDocumentException ex)
            {
                store.Delete(folder);
                return new UnlockResult(null, ex.Failure is PdfOpenFailure.WrongPassword or PdfOpenFailure.PasswordRequired ? UnlockFailure.WrongPassword : UnlockFailure.Unreadable);
            }

            if (!document.Features.HasEveryPermission || document.PageCount == 0)
            {
                var failure = document.PageCount == 0 ? UnlockFailure.Unreadable : UnlockFailure.NotOwnerPassword;
                await document.DisposeAsync();
                store.Delete(folder);
                return new UnlockResult(null, failure);
            }

            var unlocked = await UnlockAsync(document, copy, unlock, cancellationToken);
            if (unlocked.Document is not { } plain)
            {
                store.Delete(folder);
                return new UnlockResult(null, UnlockFailure.NotOwnerPassword);
            }

            var workspace = await StartAsync(folder, fullPath, Path.GetFileName(fullPath), stamp, copy, plain, unlocked.Vault, unlocked.Protection, openPassword: null, cancellationToken);
            return new UnlockResult(workspace, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or PdfDocumentException)
        {
            _logger.LogWarning("Opening a protected document for editing failed: {ErrorType}", ex.GetType().Name);
            store.Delete(folder);
            return new UnlockResult(null, UnlockFailure.Unreadable);
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

    /// <summary>A new session folder holding a copy of the file, or why it could not be copied.</summary>
    private async Task<(SessionFolder? Folder, PdfOpenFailure Failure)> CreateFolderAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            return (await store.CreateAsync(fullPath, cancellationToken), default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failure = CopyFailure(ex);
            _logger.LogWarning("Copying a document into a new session failed: {Failure} ({ErrorType})", failure, ex.GetType().Name);
            return (null, failure);
        }
    }

    /// <summary>
    /// Writes the session copy of a file opened with its owner password again without encryption and opens it. The
    /// protection it had (algorithm, permissions, whether it takes an open password) is what it is saved with, until
    /// changed. Null document when PDFsharp does not take the password as the owner's (the file stays view-only).
    /// </summary>
    private async Task<(IPdfDocument? Document, ProtectionPasswordVault Vault, ProtectionSetting? Protection)> UnlockAsync(
        IPdfDocument document,
        string copy,
        ProtectionUnlock unlock,
        CancellationToken cancellationToken)
    {
        var features = document.Features;
        await document.DisposeAsync();
        var vault = new ProtectionPasswordVault();
        try
        {
            await UnprotectDocumentOperation.WriteAsync(copy, unlock.OwnerPassword, copy, cancellationToken);
        }
        catch (DocumentPasswordException ex)
        {
            _logger.LogWarning("A protected document stays view-only: {Problem}", ex.Problem);
            return (null, vault, null);
        }

        var plain = await loader.LoadAsync(copy, password: null, cancellationToken);
        var passwords = new DocumentPasswords(unlock.HasOpenPassword ? unlock.OpenPassword : null, unlock.OwnerPassword);
        var protection = ProtectionSetting.OfFile(vault.Add(passwords), features.SecurityHandlerRevision, PermissionBits.ToPermissions(features.UserPermissions), unlock.HasOpenPassword);
        _logger.LogInformation("A protected document was opened for editing (security revision {Revision})", features.SecurityHandlerRevision);
        return (plain, vault, protection);
    }

    private async Task<DocumentWorkspace> StartAsync(
        SessionFolder folder,
        string fullPath,
        string fileName,
        FileStamp? stamp,
        string copy,
        IPdfDocument document,
        ProtectionPasswordVault vault,
        ProtectionSetting? protection,
        string? openPassword,
        CancellationToken cancellationToken)
    {
        var source = new SessionSource(SourceIds.Original, SessionStore.SourceFileName(SourceIds.Original), SessionSourceKind.Original, document.PageCount, fileName);
        var remembered = stamp is { } known && !document.Features.IsEncrypted && protection is null
            ? await splitMarks.RecallAsync(fullPath, known, document.PageCount, cancellationToken)
            : [];
        var plan = PagePlan.Identity(SourceIds.Original, document.PageCount, RandomPageKeys.Instance)
            .WithSplitMarks(remembered.ToDictionary(m => m.Page, m => new SplitMark(m.FileName)))
            .WithProtection(protection);
        var session = new DocumentSession(folder.Id, folder.Directory, fullPath, [source], new SessionState(plan, SourceIds.Original, 0));
        return new DocumentWorkspace(
            session,
            store,
            folder,
            [new OpenSource(source, copy, document)],
            stamp,
            DateTimeOffset.UtcNow,
            loggers.CreateLogger<DocumentWorkspace>(),
            splitMarks,
            vault,
            document.Features.IsEncrypted ? openPassword : null);
    }
}
