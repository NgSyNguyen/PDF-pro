using PdfPro.Core.Commands;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Sessions;

/// <summary>
/// A history step that switches the session between two immutable states, so undo and redo are instant whatever
/// the document size. <see cref="Record"/> describes the step for the manifest.
/// </summary>
public abstract class SessionStateCommand : IUndoableCommand<DocumentSession>
{
    protected SessionStateCommand(string description, SessionState before, SessionState after, HistoryRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(record);
        Description = description;
        Before = before;
        After = after;
        Record = record;
    }

    public string Description { get; }

    public SessionState Before { get; }

    public SessionState After { get; }

    public HistoryRecord Record { get; }

    public void Do(DocumentSession context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Expect(context, Before);
        context.ApplyState(After);
    }

    public void Undo(DocumentSession context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Expect(context, After);
        context.ApplyState(Before);
    }

    private static void Expect(DocumentSession session, SessionState expected)
    {
        if (!Equals(session.State, expected))
        {
            throw new InvalidOperationException("The document is not in the state this command was made for.");
        }
    }
}

/// <summary>A page plan edit (reorder, rotate, delete, insert, crop): the base source and revision stay the same.</summary>
public sealed class PlanCommand : SessionStateCommand
{
    public PlanCommand(string description, SessionState before, SessionState after, HistoryRecord record)
        : base(description, before, after, record)
    {
        if (!string.Equals(before.BaseSourceId, after.BaseSourceId, StringComparison.Ordinal) || before.Revision != after.Revision)
        {
            throw new ArgumentException("A plan edit must not change the base source or the revision.", nameof(after));
        }

        if (record.NewSourceId is not null)
        {
            throw new ArgumentException("A plan edit does not produce a source.", nameof(record));
        }
    }

    /// <summary>The edit from <paramref name="before"/> to the same state with <paramref name="plan"/>.</summary>
    public static PlanCommand Create(string description, SessionState before, PagePlan plan, HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(before);
        return new PlanCommand(description, before, before.WithPlan(plan), record);
    }
}

/// <summary>
/// A content change (page size, watermark, OCR…) that produced a new revision file: <see cref="SessionState.Revision"/>
/// goes up and the plan now shows the revision's pages. Undo returns to the previous revision and its plan.
/// </summary>
public sealed class RevisionCommand : SessionStateCommand
{
    public RevisionCommand(string description, SessionState before, SessionState after, HistoryRecord record)
        : base(description, before, after, record)
    {
        if (after.Revision <= before.Revision)
        {
            throw new ArgumentException("A content change must produce a newer revision.", nameof(after));
        }

        if (!string.Equals(record.NewSourceId, after.BaseSourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The record must name the revision source the change produced.", nameof(record));
        }
    }
}
