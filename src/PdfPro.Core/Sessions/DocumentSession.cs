using PdfPro.Core.Commands;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Sessions;

/// <summary>
/// An open document being edited (SPEC "DocumentSession và Revision"): the source files copied into the session
/// folder, the current <see cref="SessionState"/> (page plan, base source, revision) and the undo/redo history.
/// The original file is not touched until the user saves. Not thread-safe: use it from the UI thread only; hand
/// <see cref="State"/> (immutable) to background work.
/// </summary>
/// <remarks>
/// The state only changes through <see cref="SessionStateCommand"/>s executed on <see cref="History"/>, so every
/// change can be undone. This class does no I/O: the session folder is managed by the infrastructure layer.
/// </remarks>
public sealed class DocumentSession
{
    private readonly Dictionary<string, SessionSource> _sources;
    private bool _forcedDirty;
    private bool _lastDirty;

    /// <param name="id">Session id (also the session folder name).</param>
    /// <param name="directory">The session folder.</param>
    /// <param name="originalPath">The file the document was opened from and is saved to; null for a new document.</param>
    /// <param name="sources">The files the plan may draw pages from.</param>
    /// <param name="initialState">The state when the session starts (the bottom of the undo history).</param>
    /// <param name="maxUndoDepth">How many steps can be undone.</param>
    public DocumentSession(
        Guid id,
        string directory,
        string? originalPath,
        IEnumerable<SessionSource> sources,
        SessionState initialState,
        int maxUndoDepth = UndoRedoStack<DocumentSession>.DefaultMaxDepth)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A session id must not be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (originalPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        }

        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(initialState);
        _sources = new Dictionary<string, SessionSource>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source, nameof(sources));
            if (!_sources.TryAdd(source.Id, source))
            {
                throw new ArgumentException($"Duplicate source id '{source.Id}'.", nameof(sources));
            }
        }

        Validate(initialState, nameof(initialState));
        Id = id;
        Directory = directory;
        OriginalPath = originalPath;
        State = initialState;
        History = new UndoRedoStack<DocumentSession>(this, maxUndoDepth);
        History.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoRedoStack<DocumentSession>.IsAtSavePoint))
            {
                RaiseDirtyChangedIfNeeded();
            }
        };
        _lastDirty = IsDirty;
    }

    /// <summary>Raised after every change of <see cref="State"/> (edit, undo, redo).</summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised after a source file was added.</summary>
    public event EventHandler? SourcesChanged;

    /// <summary>Raised when <see cref="IsDirty"/> changes.</summary>
    public event EventHandler? DirtyChanged;

    public Guid Id { get; }

    public string Directory { get; }

    /// <summary>Where the document is saved; changes with "Save as".</summary>
    public string? OriginalPath { get; private set; }

    public IReadOnlyDictionary<string, SessionSource> Sources => _sources;

    public SessionState State { get; private set; }

    public PagePlan Plan => State.Plan;

    public string BaseSourceId => State.BaseSourceId;

    public int CurrentRevision => State.Revision;

    public UndoRedoStack<DocumentSession> History { get; }

    /// <summary>True when the document differs from what was last saved (the tab shows "*").</summary>
    public bool IsDirty => _forcedDirty || !History.IsAtSavePoint;

    /// <summary>Adds a file to draw pages from (an inserted file or a revision). Sources are never removed.</summary>
    public void AddSource(SessionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_sources.TryAdd(source.Id, source))
        {
            throw new ArgumentException($"A source with id '{source.Id}' already exists.", nameof(source));
        }

        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A free id for a new source: "s1", "s2"… for inserted files, "r1", "r2"… for revisions.</summary>
    public string NextSourceId(SessionSourceKind kind)
        => kind switch
        {
            SessionSourceKind.Inserted => SourceIds.Next('s', _sources.Keys, first: 1),
            SessionSourceKind.Revision => SourceIds.Next('r', _sources.Keys, first: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only inserted files and revisions get new ids."),
        };

    /// <summary>After "Save as": later saves go to <paramref name="path"/>.</summary>
    public void SetOriginalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        OriginalPath = path;
    }

    /// <summary>Records the current state as saved.</summary>
    public void MarkSaved()
    {
        _forcedDirty = false;
        History.MarkSavePoint();
        RaiseDirtyChangedIfNeeded();
    }

    /// <summary>
    /// Marks the document as unsaved even if the history says otherwise (a recovered session whose saved state is no
    /// longer in the history). Cleared by <see cref="MarkSaved"/>.
    /// </summary>
    public void MarkUnsaved()
    {
        _forcedDirty = true;
        RaiseDirtyChangedIfNeeded();
    }

    /// <summary>Switches to <paramref name="state"/>; only <see cref="SessionStateCommand"/> calls this.</summary>
    internal void ApplyState(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state, nameof(state));
        var old = State;
        State = state;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(old, state));
    }

    /// <summary>Every page must come from a known source and exist in it.</summary>
    private void Validate(SessionState state, string parameterName)
    {
        if (!_sources.ContainsKey(state.BaseSourceId))
        {
            throw new ArgumentException($"Unknown base source '{state.BaseSourceId}'.", parameterName);
        }

        foreach (var page in state.Plan)
        {
            if (page.IsBlank)
            {
                continue;
            }

            if (!_sources.TryGetValue(page.SourceId, out var source))
            {
                throw new ArgumentException($"The plan uses unknown source '{page.SourceId}'.", parameterName);
            }

            if (page.SourcePageIndex >= source.PageCount)
            {
                throw new ArgumentException($"Source '{page.SourceId}' has no page {page.SourcePageIndex + 1}.", parameterName);
            }
        }
    }

    private void RaiseDirtyChangedIfNeeded()
    {
        var dirty = IsDirty;
        if (dirty != _lastDirty)
        {
            _lastDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
