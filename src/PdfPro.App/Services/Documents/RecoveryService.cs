using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>A document with unsaved changes left by a crash, as listed in the recovery dialog.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="FileName">The document's file name.</param>
/// <param name="Folder">The folder of its file.</param>
/// <param name="LastChange">When it last changed (local time).</param>
/// <param name="Changes">How many changes can be undone.</param>
public sealed record RecoveryItem(Guid SessionId, string FileName, string Folder, DateTime LastChange, int Changes);

public enum RecoveryAction
{
    /// <summary>Restore the selected documents; the others are discarded.</summary>
    Restore,

    /// <summary>Discard every listed document (confirmed by the user).</summary>
    DiscardAll,

    /// <summary>Keep everything and ask again at the next start.</summary>
    Later,
}

/// <param name="Action">What the user chose.</param>
/// <param name="Selected">With <see cref="RecoveryAction.Restore"/>: the sessions to restore.</param>
public sealed record RecoveryDecision(RecoveryAction Action, IReadOnlySet<Guid> Selected)
{
    public static RecoveryDecision Later { get; } = new(RecoveryAction.Later, new HashSet<Guid>());
}

/// <summary>The "Khôi phục tài liệu chưa lưu" dialog (seam for tests).</summary>
public interface IRecoveryDialog
{
    RecoveryDecision Ask(IReadOnlyList<RecoveryItem> items);
}

/// <summary>Finds and restores documents left unsaved by a crash (seam for view-model tests).</summary>
public interface IRecoveryService
{
    /// <summary>Restored documents, ready to open in tabs (empty when there was nothing to recover).</summary>
    Task<IReadOnlyList<DocumentWorkspace>> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Crash recovery at startup (SPEC P3): session folders whose owner process is gone are examined in the background.
/// A save interrupted in the middle of its file swap is repaired first (the previous file is put back if it was moved
/// aside). Sessions without unsaved changes are removed silently; the others are offered to the user, and the chosen
/// ones restored with their undo history. Must be called on the UI thread. Logs name sessions by id only.
/// </summary>
internal sealed class RecoveryService(
    SessionStore store,
    IPdfDocumentLoader loader,
    OperationCatalog operations,
    IRecoveryDialog dialog,
    IMessageService messages,
    ILoggerFactory loggers) : IRecoveryService
{
    private readonly ILogger _logger = loggers.CreateLogger<RecoveryService>();

    public async Task<IReadOnlyList<DocumentWorkspace>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await Task.Run(FindRecoverable, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        _logger.LogInformation("{Count} document(s) with unsaved changes can be recovered", candidates.Count);
        var decision = dialog.Ask([.. candidates.Select(ToItem)]);
        if (decision.Action == RecoveryAction.Later)
        {
            candidates.ForEach(c => c.Folder.Dispose()); // asked again at the next start
            _logger.LogInformation("Recovery postponed");
            return [];
        }

        var discarded = candidates.Where(c => decision.Action == RecoveryAction.DiscardAll || !decision.Selected.Contains(c.Folder.Id)).ToList();
        if (discarded.Count > 0)
        {
            _logger.LogInformation("{Count} unsaved document(s) discarded", discarded.Count);
            await Task.Run(() => discarded.ForEach(c => store.Delete(c.Folder)), CancellationToken.None);
        }

        var restored = new List<DocumentWorkspace>();
        foreach (var candidate in candidates.Except(discarded))
        {
            try
            {
                restored.Add(await RestoreAsync(candidate, cancellationToken));
            }
            catch (Exception ex) when (ex is PdfDocumentException or SessionManifestException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Exception texts may contain paths: log the type only. The session stays for the next start.
                _logger.LogWarning("Session {SessionId} could not be restored: {ErrorType}", candidate.Folder.Id, ex.GetType().Name);
                candidate.Folder.Dispose();
                await messages.ShowErrorAsync(Strings.Recovery_Title, string.Format(CultureInfo.CurrentCulture, Strings.Recovery_Failed, ToItem(candidate).FileName));
            }
        }

        return restored;
    }

    /// <summary>
    /// Orphaned sessions that hold unsaved changes. The others are removed, after repairing an interrupted save.
    /// Runs in the background (file system work).
    /// </summary>
    private List<OrphanSession> FindRecoverable()
    {
        var candidates = new List<OrphanSession>();
        foreach (var orphan in store.FindOrphans())
        {
            if (orphan.Manifest is not { } manifest)
            {
                _logger.LogInformation("Session {SessionId} has no readable manifest and is removed", orphan.Folder.Id);
                store.Delete(orphan.Folder);
                continue;
            }

            if (manifest.PendingSave is { } pending)
            {
                RepairInterruptedSave(orphan.Folder.Id, pending);
            }

            if (manifest.IsDirty)
            {
                candidates.Add(orphan);
            }
            else
            {
                store.Delete(orphan.Folder);
            }
        }

        return candidates;
    }

    private static RecoveryItem ToItem(OrphanSession orphan)
    {
        var manifest = orphan.Manifest!;
        var path = manifest.OriginalPath ?? manifest.Sources.FirstOrDefault()?.DisplayName ?? string.Empty;
        var fileName = Path.GetFileName(path);
        return new RecoveryItem(
            orphan.Folder.Id,
            string.IsNullOrEmpty(fileName) ? Strings.Recovery_Untitled : fileName,
            Path.GetDirectoryName(path) ?? string.Empty,
            orphan.LastWriteUtc.ToLocalTime(),
            manifest.Undo.Count);
    }

    private void RepairInterruptedSave(Guid sessionId, PendingSaveDto pending)
    {
        try
        {
            var outcome = AtomicFile.RecoverInterruptedSwap(pending.TargetPath, pending.TempPath, pending.BackupPath);
            _logger.LogWarning("Session {SessionId} was saving when the app stopped: {Outcome}", sessionId, outcome);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Repairing an interrupted save of session {SessionId} failed: {ErrorType}", sessionId, ex.GetType().Name);
        }
    }

    private async Task<DocumentWorkspace> RestoreAsync(OrphanSession orphan, CancellationToken cancellationToken)
    {
        var manifest = orphan.Manifest!;
        var snapshot = ReadSnapshot(orphan.Folder.Id, manifest, out var historyLost);
        var sources = new List<OpenSource>();
        try
        {
            foreach (var source in snapshot.Sources)
            {
                var path = orphan.Folder.PathOf(source.FileName);
                var document = await loader.LoadAsync(path, password: null, cancellationToken);
                sources.Add(new OpenSource(source, path, document));
            }

            var sizes = sources.ToDictionary(s => s.Source.Id, s => s.Document.PageSizes, StringComparer.Ordinal);
            var session = SessionReplay.Rebuild(snapshot, orphan.Folder.Directory, sizes, operations, out var replayFailed);
            if (historyLost || replayFailed)
            {
                _logger.LogWarning("Session {SessionId} restored without its undo history", orphan.Folder.Id);
            }

            var stamp = manifest.OriginalStamp is { } s ? new FileStamp(s.Length, s.LastWriteTimeUtc) : (FileStamp?)null;
            _logger.LogInformation("Session {SessionId} restored: {PageCount} pages, {Steps} undo step(s)", orphan.Folder.Id, session.Plan.Count, session.History.UndoCount);
            return new DocumentWorkspace(session, store, orphan.Folder, sources, stamp, manifest.CreatedUtc, loggers.CreateLogger<DocumentWorkspace>());
        }
        catch
        {
            foreach (var source in sources)
            {
                await source.Document.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>
    /// The manifest as a snapshot. A history step this version does not know (written by another version of the app)
    /// costs the history, not the document: the current state is restored on its own, marked unsaved.
    /// </summary>
    private SessionSnapshot ReadSnapshot(Guid sessionId, SessionManifest manifest, out bool historyLost)
    {
        try
        {
            historyLost = false;
            return SessionManifestSerializer.ToSnapshot(manifest, operations.ParameterType);
        }
        catch (SessionManifestException) when (manifest.Undo.Count + manifest.Redo.Count > 0)
        {
            _logger.LogWarning("The history of session {SessionId} cannot be read; only its current state is restored", sessionId);
            historyLost = true;
            var current = manifest with { HistoryBase = manifest.State, Undo = [], Redo = [], SavePoint = null, ForcedDirty = true };
            return SessionManifestSerializer.ToSnapshot(current, operations.ParameterType);
        }
    }
}
