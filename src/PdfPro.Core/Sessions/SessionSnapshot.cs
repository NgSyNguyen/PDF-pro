namespace PdfPro.Core.Sessions;

/// <summary>
/// Everything needed to rebuild a document session after a crash: sources, current state and the undo/redo history
/// as replayable records. Immutable; taken on the UI thread (cheap: states and records are shared) and written to
/// the session manifest in the background.
/// </summary>
public sealed record SessionSnapshot
{
    public required Guid SessionId { get; init; }

    public string? OriginalPath { get; init; }

    public required IReadOnlyList<SessionSource> Sources { get; init; }

    public required SessionState State { get; init; }

    /// <summary>The state before the oldest undoable step: replaying <see cref="Undo"/> from here reaches <see cref="State"/>.</summary>
    public required SessionState HistoryBase { get; init; }

    /// <summary>Undoable steps, oldest first.</summary>
    public required IReadOnlyList<HistoryRecord> Undo { get; init; }

    /// <summary>Redoable steps, next redo first; replayed after <see cref="Undo"/>, then undone again.</summary>
    public required IReadOnlyList<HistoryRecord> Redo { get; init; }

    /// <summary>Position of the saved state in the history (see <c>UndoRedoStack.SavePointPosition</c>), or null.</summary>
    public int? SavePoint { get; init; }

    public bool ForcedDirty { get; init; }

    /// <summary>True when the state differs from the saved file.</summary>
    public bool IsDirty => ForcedDirty || SavePoint != Undo.Count;
}
