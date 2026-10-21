using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Threading;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>A source file of a session: its description, its copy in the session folder and its opened document.</summary>
public sealed record OpenSource(SessionSource Source, string Path, IPdfDocument Document);

/// <summary>
/// An open document (SPEC "DocumentSession và Revision"): its session (page plan and undo/redo history), the locked
/// session folder holding copies of its sources, their PDFium documents and the <see cref="VirtualDocument"/> the views
/// show. Every change is one undoable step and is written to the session manifest in the background, so a crash loses
/// nothing. The original file is only touched when the user saves. Must be used on the UI thread.
/// </summary>
public sealed class DocumentWorkspace : IAsyncDisposable
{
    private readonly SessionStore _store;
    private readonly SessionFolder _folder;
    private readonly Dictionary<string, OpenSource> _sources = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly CoalescingWorker _manifestWriter;
    private readonly DateTimeOffset _createdUtc;
    private ManifestState? _latest;
    private PendingSaveDto? _pendingSave;
    private bool _isBusy;
    private bool _disposed;

    internal DocumentWorkspace(
        DocumentSession session,
        SessionStore store,
        SessionFolder folder,
        IEnumerable<OpenSource> sources,
        FileStamp? originalStamp,
        DateTimeOffset createdUtc,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sources);
        Session = session;
        _store = store;
        _folder = folder;
        _logger = logger;
        _createdUtc = createdUtc;
        OriginalStamp = originalStamp;
        foreach (var source in sources)
        {
            _sources.Add(source.Source.Id, source);
        }

        Pages = VirtualDocument.Create(session.Plan, Documents());
        _manifestWriter = new CoalescingWorker(WriteManifestAsync, logger);
        session.StateChanged += OnSessionStateChanged;
        session.History.StateChanged += OnHistoryChanged;
        session.DirtyChanged += OnDirtyChanged;
        ScheduleManifest();
    }

    /// <summary>The plan changed (edit, undo, redo): <see cref="Pages"/> is new.</summary>
    public event EventHandler? PagesChanged;

    /// <summary>The history, the dirty flag or <see cref="IsBusy"/> changed: commands should be re-evaluated.</summary>
    public event EventHandler? StatusChanged;

    public DocumentSession Session { get; }

    /// <summary>The document as the plan shows it.</summary>
    public VirtualDocument Pages { get; private set; }

    /// <summary>The source whose structure the saved file keeps (the opened file, or the latest revision).</summary>
    public IPdfDocument BaseDocument => _sources[Session.BaseSourceId].Document;

    /// <summary>The opened file itself (source "s0").</summary>
    public IPdfDocument OriginalDocument => _sources[SourceIds.Original].Document;

    /// <summary>Where the document is saved.</summary>
    public string? FilePath => Session.OriginalPath;

    public string FileName => Path.GetFileName(FilePath) ?? _sources[SourceIds.Original].Source.DisplayName ?? string.Empty;

    /// <summary>The original file as opened or last saved (to notice changes by other programs).</summary>
    public FileStamp? OriginalStamp { get; private set; }

    /// <summary>
    /// Password protected documents are view-only for now: saving them would need the owner password to encrypt the
    /// result again (Phase 8, security).
    /// </summary>
    public bool IsReadOnly => OriginalDocument.Features.IsEncrypted;

    /// <summary>A save is running: edits are refused so the saved state matches the save point.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool CanEdit => !IsReadOnly && !IsBusy && !_disposed;

    public bool IsDirty => Session.IsDirty;

    public IReadOnlyCollection<OpenSource> Sources => _sources.Values;

    /// <summary>The session folder's scratch space (repaired sources, materialized files in progress).</summary>
    public string WorkDirectory => _folder.WorkDirectory;

    /// <summary>Displayed page sizes of every source, for plan operations.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> SourcePageSizes
        => _sources.ToDictionary(s => s.Key, s => s.Value.Document.PageSizes, StringComparer.Ordinal);

    /// <summary>File paths of the sources, by id (for the materializer).</summary>
    public IReadOnlyDictionary<string, string> SourcePaths
        => _sources.ToDictionary(s => s.Key, s => s.Value.Path, StringComparer.Ordinal);

    /// <summary>The copy of the source file inside the session folder.</summary>
    public string? SourcePathOf(IPdfDocument document)
        => _sources.Values.FirstOrDefault(s => ReferenceEquals(s.Document, document))?.Path;

    /// <summary>
    /// Applies a page operation as one undoable step. Returns false when it changes nothing (no step is recorded).
    /// </summary>
    /// <exception cref="InvalidOperationException">The document cannot be edited (read-only or saving).</exception>
    /// <exception cref="Core.Operations.OperationValidationException">The parameters do not fit the document.</exception>
    public bool Apply(IPlanOperation operation, object parameters)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureEditable();
        var keys = new RecordingPageKeys(RandomPageKeys.Instance);
        var before = Session.Plan;
        var plan = operation.ApplyToPlan(before, parameters, new PlanEditContext(SourcePageSizes, keys));
        if (ReferenceEquals(plan, before))
        {
            return false;
        }

        var record = new HistoryRecord(operation.Id, parameters, [.. keys.Keys]);
        Session.History.Execute(PlanCommand.Create(Describe(record, before), Session.State, plan, record));
        return true;
    }

    /// <summary>
    /// Copies a PDF file into the session folder and opens the copy as a new source ("s1", "s2"…) whose pages can then
    /// be inserted. The source stays in the session (an undone insert can be redone, a crash restores it). Password
    /// protected files are refused for now (Phase 8, security).
    /// </summary>
    /// <param name="path">The file to add.</param>
    /// <param name="loader">Opens the copy.</param>
    /// <param name="cancellationToken">Cancels copying and opening; nothing is added then.</param>
    /// <exception cref="PdfDocumentException">The file cannot be opened, or it is password protected
    /// (<see cref="PdfOpenFailure.PasswordRequired"/>).</exception>
    /// <exception cref="IOException">The file could not be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    public async Task<OpenSource> AddSourceAsync(string path, IPdfDocumentLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(loader);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Session.NextSourceId(SessionSourceKind.Inserted);
        var fileName = SessionStore.SourceFileName(id);
        var copy = _folder.PathOf(fileName);
        IPdfDocument? document = null;
        try
        {
            await _store.CopySourceAsync(_folder, id, path, cancellationToken);
            document = await loader.LoadAsync(copy, password: null, cancellationToken);
            if (document.Features.IsEncrypted)
            {
                throw new PdfPasswordRequiredException();
            }

            if (document.PageCount == 0)
            {
                throw new PdfDocumentException(PdfOpenFailure.Corrupted, "The PDF document has no pages.");
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        catch
        {
            if (document is not null)
            {
                await document.DisposeAsync();
            }

            TryDelete(copy);
            throw;
        }

        var source = new SessionSource(id, fileName, SessionSourceKind.Inserted, document.PageCount, Path.GetFileName(path));
        var open = new OpenSource(source, copy, document);
        Session.AddSource(source);
        _sources.Add(id, open);
        _logger.LogInformation("Session {SessionId}: source {SourceId} added ({PageCount} pages)", Session.Id, id, document.PageCount);
        ScheduleManifest();
        return open;
    }

    /// <summary>Undoes <paramref name="steps"/> steps (the history menu jumps several at once).</summary>
    public void Undo(int steps = 1)
    {
        EnsureEditable();
        for (var i = 0; i < steps && Session.History.CanUndo; i++)
        {
            Session.History.Undo();
        }
    }

    public void Redo(int steps = 1)
    {
        EnsureEditable();
        for (var i = 0; i < steps && Session.History.CanRedo; i++)
        {
            Session.History.Redo();
        }
    }

    /// <summary>The localized name of a history step (used when restoring a session too).</summary>
    public string Describe(HistoryRecord record, PagePlan before)
        => HistoryDescriptions.Describe(record, before, id => Session.Sources.GetValueOrDefault(id)?.DisplayName);

    /// <summary>Writes the manifest now and waits for it (before swapping files during a save).</summary>
    public async Task FlushManifestAsync()
    {
        ScheduleManifest();
        await _manifestWriter.FlushAsync();
    }

    /// <summary>
    /// Closes the document: stops background writes, closes the source documents and removes the session folder.
    /// Ask the user about unsaved changes before calling this.
    /// </summary>
    public ValueTask DisposeAsync() => CloseAsync(keepSession: false);

    /// <summary>
    /// Closes the document. With <paramref name="keepSession"/> the session folder stays on disk (its lock is
    /// released) so the unsaved changes are offered for recovery at the next start, e.g. when Windows shuts down.
    /// </summary>
    public async ValueTask CloseAsync(bool keepSession)
    {
        if (_disposed)
        {
            return;
        }

        if (keepSession)
        {
            ScheduleManifest(); // the last state must be on disk
        }

        _disposed = true;
        Session.StateChanged -= OnSessionStateChanged;
        Session.History.StateChanged -= OnHistoryChanged;
        Session.DirtyChanged -= OnDirtyChanged;
        // No UI work below: never come back to the UI thread, which may be blocked waiting for this (application exit).
        await _manifestWriter.DisposeAsync().ConfigureAwait(false);
        foreach (var source in _sources.Values)
        {
            await source.Document.DisposeAsync().ConfigureAwait(false);
        }

        if (keepSession)
        {
            _folder.Dispose();
            _logger.LogInformation("Session {SessionId} kept for recovery", Session.Id);
        }
        else
        {
            _store.Delete(_folder);
        }
    }

    /// <summary>True when the saved file was changed by another program since it was opened or last saved.</summary>
    public bool HasChangedOnDisk()
        => FilePath is { } path && OriginalStamp is { } known && FileStamp.Read(path) is { } current && current != known;

    /// <summary>
    /// A save is about to write: the manifest records its files first, so a crash while writing or swapping can be
    /// repaired at the next start. Safe from any thread (it reuses the last snapshot: edits are blocked while saving).
    /// </summary>
    internal async Task RecordPendingSaveAsync(PendingSaveDto pendingSave)
    {
        _pendingSave = pendingSave;
        if (Volatile.Read(ref _latest) is { } latest)
        {
            Volatile.Write(ref _latest, latest with { PendingSave = pendingSave });
            _manifestWriter.Request();
            await _manifestWriter.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The save finished (or failed): the manifest no longer mentions it.</summary>
    internal async Task ClearPendingSaveAsync()
    {
        if (_pendingSave is null)
        {
            return;
        }

        _pendingSave = null;
        await FlushManifestAsync();
    }

    /// <summary>
    /// The document was written to <paramref name="path"/>: it becomes the document's file ("Save as"), its stamp is
    /// remembered and the current state is the saved one.
    /// </summary>
    internal void CompleteSave(string path, FileStamp? stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Session.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Session.SetOriginalPath(path);
        }

        OriginalStamp = stamp;
        Session.MarkSaved();
        ScheduleManifest();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, IPdfDocument> Documents() => _sources.ToDictionary(s => s.Key, s => s.Value.Document, StringComparer.Ordinal);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("A source copy that was not added could not be removed: {ErrorType}", ex.GetType().Name);
        }
    }

    private void EnsureEditable()
    {
        if (!CanEdit)
        {
            throw new InvalidOperationException("The document cannot be edited right now.");
        }
    }

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        Pages = VirtualDocument.Create(e.NewState.Plan, Documents());
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        ScheduleManifest();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDirtyChanged(object? sender, EventArgs e) => StatusChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Takes a snapshot (cheap: shared immutable state) and asks the background writer to save it.</summary>
    private void ScheduleManifest()
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _latest, new ManifestState(Session.CreateSnapshot(), OriginalStamp, _pendingSave));
        _manifestWriter.Request();
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _latest) is not { } state)
        {
            return;
        }

        var manifest = SessionManifestSerializer.ToManifest(state.Snapshot, _createdUtc, state.Stamp, state.PendingSave);
        await _store.WriteManifestAsync(_folder, manifest, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ManifestState(SessionSnapshot Snapshot, FileStamp? Stamp, PendingSaveDto? PendingSave);
}
