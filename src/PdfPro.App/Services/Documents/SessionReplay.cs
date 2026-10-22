using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations;

namespace PdfPro.App.Services.Documents;

/// <summary>
/// Rebuilds a crashed session from its manifest snapshot: starting at the history base, every recorded step is
/// replayed (with the page keys it created), the save point is restored and the redo steps are undone again, so the
/// user gets the exact document and undo/redo history back. When the steps do not replay exactly (unknown operation,
/// changed rules), the current state is restored without history and marked unsaved — the document is never lost.
/// </summary>
internal static class SessionReplay
{
    /// <param name="snapshot">The session as written to the manifest.</param>
    /// <param name="directory">The session folder.</param>
    /// <param name="sourcePageSizes">Displayed page sizes of every source, from the reopened files.</param>
    /// <param name="operations">The operations that made the steps.</param>
    /// <param name="historyLost">True when only the current state could be restored.</param>
    public static DocumentSession Rebuild(
        SessionSnapshot snapshot,
        string directory,
        IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> sourcePageSizes,
        OperationCatalog operations,
        out bool historyLost)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(operations);
        try
        {
            var session = new DocumentSession(snapshot.SessionId, directory, snapshot.OriginalPath, snapshot.Sources, snapshot.HistoryBase);
            var records = snapshot.Undo.Concat(snapshot.Redo).ToArray();
            for (var i = 0; i < records.Length; i++)
            {
                Execute(session, records[i], sourcePageSizes, operations);
                if (snapshot.SavePoint == i + 1)
                {
                    session.MarkSaved();
                }
            }

            for (var i = 0; i < snapshot.Redo.Count; i++)
            {
                session.History.Undo();
            }

            if (snapshot.ForcedDirty || snapshot.SavePoint is null || snapshot.SavePoint > records.Length)
            {
                session.MarkUnsaved(); // the saved state is not in the history any more
            }

            if (!SameState(session.State, snapshot.State))
            {
                throw new InvalidOperationException("The replayed history does not reach the recorded state.");
            }

            historyLost = false;
            return session;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OperationValidationException)
        {
            historyLost = true;
            var session = new DocumentSession(snapshot.SessionId, directory, snapshot.OriginalPath, snapshot.Sources, snapshot.State);
            session.MarkUnsaved();
            return session;
        }
    }

    /// <summary>Re-runs one recorded step on the session, with the keys it created.</summary>
    internal static void Execute(
        DocumentSession session,
        HistoryRecord record,
        IReadOnlyDictionary<string, IReadOnlyList<PdfSize>> sourcePageSizes,
        OperationCatalog operations)
    {
        var before = session.State;
        var keys = new ReplayPageKeys(record.NewPageKeys);
        var description = HistoryDescriptions.Describe(record, before.Plan, id => session.Sources.GetValueOrDefault(id)?.DisplayName);
        if (record.NewSourceId is { } revisionSource)
        {
            // A content operation: the plan became every page of the revision it produced.
            var source = session.Sources.GetValueOrDefault(revisionSource)
                ?? throw new InvalidOperationException($"The revision source '{revisionSource}' is missing.");
            var plan = PagePlan.Identity(revisionSource, source.PageCount, keys);
            session.History.Execute(new RevisionCommand(description, before, before.NextRevision(revisionSource, plan), record));
        }
        else
        {
            var operation = operations.FindPlanOperation(record.OperationId)
                ?? throw new InvalidOperationException($"Unknown operation '{record.OperationId}'.");
            var plan = operation.ApplyToPlan(before.Plan, record.Parameters, new PlanEditContext(sourcePageSizes, keys));
            session.History.Execute(PlanCommand.Create(description, before, plan, record, operation.ChangesContent));
        }

        if (!keys.IsExhausted)
        {
            throw new InvalidOperationException("The replayed step created fewer pages than were recorded.");
        }
    }

    private static bool SameState(SessionState a, SessionState b)
        => a.BaseSourceId == b.BaseSourceId && a.Revision == b.Revision && a.Plan.SequenceEqual(b.Plan);
}
