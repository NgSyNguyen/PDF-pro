using System.ComponentModel;

namespace PdfPro.Core.Commands;

/// <summary>
/// Undo/redo history for one context (one document). Not thread-safe: use it from the UI thread only.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Executing a new command clears the redo branch.</item>
/// <item>When the history exceeds <see cref="MaxDepth"/>, the oldest steps are dropped.</item>
/// <item>If <c>Do</c>, <c>Undo</c> or <c>Redo</c> throws, the history is left unchanged.</item>
/// <item>A save point tracks whether the context matches what was last saved (<see cref="IsAtSavePoint"/>). Steps
/// whose <see cref="IUndoableCommand{TContext}.ChangesContent"/> is false do not move it.</item>
/// </list>
/// </remarks>
public sealed class UndoRedoStack<TContext> : INotifyPropertyChanged
{
    public const int DefaultMaxDepth = 200;

    private readonly TContext _context;
    private readonly LinkedList<Entry> _undo = new(); // Last = most recent step.
    private readonly Stack<Entry> _redo = new();

    // Every executed command gets a unique sequence number. A step's content id is its own sequence, or for a step that
    // does not change content the content id of the state it was executed on. The "state id" is the content id of the
    // newest applied step, or _baseStateId when nothing is left to undo. Comparing state ids gives the save point.
    private long _nextSequence = 1;
    private long _baseStateId;
    private long _savedStateId;
    private bool _isRunning;

    public UndoRedoStack(TContext context, int maxDepth = DefaultMaxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        _context = context;
        MaxDepth = maxDepth;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every successful change of the history.</summary>
    public event EventHandler? StateChanged;

    public int MaxDepth { get; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public string? NextUndoDescription => _undo.Last?.Value.Command.Description;

    public string? NextRedoDescription => _redo.TryPeek(out var entry) ? entry.Command.Description : null;

    /// <summary>Descriptions of the undoable steps, most recent first.</summary>
    public IReadOnlyList<string> UndoHistory => _undo.Reverse().Select(e => e.Command.Description).ToArray();

    /// <summary>Descriptions of the redoable steps, next redo first.</summary>
    public IReadOnlyList<string> RedoHistory => _redo.Select(e => e.Command.Description).ToArray();

    /// <summary>The undoable commands, oldest first (to save the history).</summary>
    public IReadOnlyList<IUndoableCommand<TContext>> UndoCommands => _undo.Select(e => e.Command).ToArray();

    /// <summary>The redoable commands, next redo first.</summary>
    public IReadOnlyList<IUndoableCommand<TContext>> RedoCommands => _redo.Select(e => e.Command).ToArray();

    /// <summary>True when the context is in the state recorded by the last <see cref="MarkSavePoint"/>.</summary>
    public bool IsAtSavePoint => CurrentStateId == _savedStateId;

    /// <summary>
    /// Where the saved state lies in the history, in steps from the oldest reachable state: 0 is the state before the
    /// oldest undoable command, <see cref="UndoCount"/> the current state and UndoCount + k the state after the k-th
    /// redo. When steps that do not change content follow the save, the first of the equivalent positions is given.
    /// Null when the saved state can no longer be reached (dropped by <see cref="MaxDepth"/> or <see cref="Clear"/>,
    /// or left on a discarded redo branch).
    /// </summary>
    public int? SavePointPosition
    {
        get
        {
            if (_savedStateId == _baseStateId)
            {
                return 0;
            }

            var position = 0;
            foreach (var entry in _undo.Concat(_redo))
            {
                position++;
                if (entry.ContentId == _savedStateId)
                {
                    return position;
                }
            }

            return null;
        }
    }

    private long CurrentStateId => _undo.Last?.Value.ContentId ?? _baseStateId;

    /// <summary>Runs <paramref name="command"/> and records it as a new undo step.</summary>
    public void Execute(IUndoableCommand<TContext> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Mutate(() =>
        {
            command.Do(_context);
            var sequence = _nextSequence++;
            _undo.AddLast(new Entry(command, sequence, command.ChangesContent ? sequence : CurrentStateId));
            _redo.Clear();
            while (_undo.Count > MaxDepth)
            {
                _baseStateId = _undo.First!.Value.ContentId;
                _undo.RemoveFirst();
            }
        });
    }

    public void Undo()
    {
        Mutate(() =>
        {
            var entry = _undo.Last?.Value ?? throw new InvalidOperationException("There is nothing to undo.");
            entry.Command.Undo(_context);
            _undo.RemoveLast();
            _redo.Push(entry);
        });
    }

    public void Redo()
    {
        Mutate(() =>
        {
            if (!_redo.TryPeek(out var entry))
            {
                throw new InvalidOperationException("There is nothing to redo.");
            }

            entry.Command.Do(_context);
            _redo.Pop();
            _undo.AddLast(entry);
        });
    }

    /// <summary>Records the current state as saved (call after a successful save).</summary>
    public void MarkSavePoint() => Mutate(() => _savedStateId = CurrentStateId);

    /// <summary>Forgets all undo/redo steps. The save point status is kept.</summary>
    public void Clear()
    {
        Mutate(() =>
        {
            _baseStateId = CurrentStateId;
            _undo.Clear();
            _redo.Clear();
        });
    }

    private void Mutate(Action change)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("The undo/redo history cannot be changed while a command is running.");
        }

        var before = Snapshot.Take(this);
        _isRunning = true;
        try
        {
            change();
        }
        finally
        {
            _isRunning = false;
        }

        var after = Snapshot.Take(this);
        RaiseIfChanged(before.CanUndo != after.CanUndo, nameof(CanUndo));
        RaiseIfChanged(before.CanRedo != after.CanRedo, nameof(CanRedo));
        RaiseIfChanged(before.UndoCount != after.UndoCount, nameof(UndoCount));
        RaiseIfChanged(before.RedoCount != after.RedoCount, nameof(RedoCount));
        RaiseIfChanged(!ReferenceEquals(before.NextUndo, after.NextUndo), nameof(NextUndoDescription));
        RaiseIfChanged(!ReferenceEquals(before.NextRedo, after.NextRedo), nameof(NextRedoDescription));
        RaiseIfChanged(before.IsAtSavePoint != after.IsAtSavePoint, nameof(IsAtSavePoint));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseIfChanged(bool changed, string propertyName)
    {
        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private readonly record struct Entry(IUndoableCommand<TContext> Command, long Sequence, long ContentId);

    private readonly record struct Snapshot(
        bool CanUndo,
        bool CanRedo,
        int UndoCount,
        int RedoCount,
        object? NextUndo,
        object? NextRedo,
        bool IsAtSavePoint)
    {
        public static Snapshot Take(UndoRedoStack<TContext> stack) => new(
            stack.CanUndo,
            stack.CanRedo,
            stack.UndoCount,
            stack.RedoCount,
            stack._undo.Last?.Value.Command,
            stack._redo.TryPeek(out var next) ? next.Command : null,
            stack.IsAtSavePoint);
    }
}
