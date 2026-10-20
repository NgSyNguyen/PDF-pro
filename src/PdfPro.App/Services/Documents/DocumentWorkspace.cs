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
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
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

        _store.Delete(_folder);
    }

    internal void SetPendingSave(PendingSaveDto? pendingSave) => _pendingSave = pendingSave;

    internal void SetOriginalStamp(FileStamp? stamp)
    {
        OriginalStamp = stamp;
        ScheduleManifest();
    }

    private Dictionary<string, IPdfDocument> Documents() => _sources.ToDictionary(s => s.Key, s => s.Value.Document, StringComparer.Ordinal);

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
