namespace PdfPro.Core.Commands;

/// <summary>
/// A user change that can be undone and redone. <typeparamref name="TContext"/> is the object the command
/// edits (the document session from Phase 3 on).
/// </summary>
public interface IUndoableCommand<in TContext>
{
    /// <summary>Localized text shown in the undo/history menu, e.g. "Xoay 3 trang".</summary>
    string Description { get; }

    /// <summary>
    /// False for a step that does not change what would be saved (marking the first pages of a split): it is undone
    /// and redone like any other step but does not move the save point, so it never makes the document "unsaved".
    /// </summary>
    bool ChangesContent => true;

    void Do(TContext context);

    void Undo(TContext context);
}
