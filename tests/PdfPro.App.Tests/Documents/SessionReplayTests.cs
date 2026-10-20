using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.Documents;

public sealed class SessionReplayTests
{
    private const string Directory = @"C:\sessions\x";
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly SessionSource Original = new(SourceIds.Original, "sources/s0.pdf", SessionSourceKind.Original, 5, "a.pdf");
    private static readonly SessionSource Revision = new("r1", "revisions/r0001.pdf", SessionSourceKind.Revision, 5);

    private static readonly Dictionary<string, IReadOnlyList<PdfSize>> Sizes = new(StringComparer.Ordinal)
    {
        [SourceIds.Original] = Enumerable.Repeat(A4, 5).ToArray(),
        ["r1"] = Enumerable.Repeat(A4, 5).ToArray(),
    };

    private readonly OperationCatalog _operations = TestOperations.Catalog();

    [Fact]
    public void Rebuild_RestoresTheStateTheHistoryAndTheSavePoint()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("1", 90));
        Apply(session, new DuplicatePagesParams("2-3"));
        session.MarkSaved();
        Apply(session, new DeletePagesParams("4"));
        Apply(session, new InsertBlankPagesParams(1, 595, 842, 2));
        session.History.Undo();

        var rebuilt = SessionReplay.Rebuild(session.CreateSnapshot(), Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeFalse();
        SamePlan(rebuilt, session).ShouldBeTrue();
        rebuilt.History.UndoCount.ShouldBe(3);
        rebuilt.History.RedoCount.ShouldBe(1);
        rebuilt.History.SavePointPosition.ShouldBe(2);
        rebuilt.IsDirty.ShouldBeTrue();
        rebuilt.OriginalPath.ShouldBe(@"C:\docs\a.pdf");
        rebuilt.History.NextUndoDescription.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_Delete, 1));

        session.History.Redo();
        rebuilt.History.Redo();
        SamePlan(rebuilt, session).ShouldBeTrue("the redo step creates the same pages with the same keys");

        rebuilt.History.Undo();
        rebuilt.History.Undo();
        rebuilt.IsDirty.ShouldBeFalse("back at the saved state");
    }

    [Fact]
    public void Rebuild_ForcedUnsaved_StaysUnsavedEverywhere()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("2", 180));
        session.MarkUnsaved();

        var rebuilt = SessionReplay.Rebuild(session.CreateSnapshot(), Directory, Sizes, _operations, out _);

        rebuilt.IsDirty.ShouldBeTrue();
        rebuilt.History.Undo();
        rebuilt.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Rebuild_SavedStateNoLongerInTheHistory_IsUnsavedEverywhere()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("1", 90));
        session.MarkSaved();
        session.History.Undo();
        Apply(session, new DeletePagesParams("5")); // drops the redo branch holding the saved state
        session.History.SavePointPosition.ShouldBeNull();

        var rebuilt = SessionReplay.Rebuild(session.CreateSnapshot(), Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeFalse();
        rebuilt.IsDirty.ShouldBeTrue();
        rebuilt.History.Undo();
        rebuilt.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Rebuild_RevisionStep_SwitchesToTheRevisionAndBack()
    {
        var session = NewSession(Original, Revision);
        Apply(session, new RotatePagesParams("1", 90));
        var keys = new RecordingPageKeys(RandomPageKeys.Instance);
        var revisionPlan = PagePlan.Identity("r1", 5, keys);
        var record = new HistoryRecord("pages.resize", new object(), [.. keys.Keys], "r1");
        session.History.Execute(new RevisionCommand("Resize", session.State, session.State.NextRevision("r1", revisionPlan), record));

        var rebuilt = SessionReplay.Rebuild(session.CreateSnapshot(), Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeFalse();
        rebuilt.BaseSourceId.ShouldBe("r1");
        rebuilt.CurrentRevision.ShouldBe(1);
        SamePlan(rebuilt, session).ShouldBeTrue();
        rebuilt.History.Undo();
        rebuilt.BaseSourceId.ShouldBe(SourceIds.Original);
        rebuilt.Plan[0].RotationDelta.ShouldBe(90);
    }

    [Fact]
    public void Rebuild_UnknownOperation_RestoresTheStateWithoutHistory()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("1", 90));
        var snapshot = session.CreateSnapshot();
        var unknown = snapshot with { Undo = [new HistoryRecord("future.feature", new object(), [])] };

        var rebuilt = SessionReplay.Rebuild(unknown, Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeTrue();
        SamePlan(rebuilt, session).ShouldBeTrue();
        rebuilt.History.CanUndo.ShouldBeFalse();
        rebuilt.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Rebuild_StepsThatDoNotReachTheRecordedState_FallBackToTheState()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("1", 90));
        var snapshot = session.CreateSnapshot();
        var mismatch = snapshot with { State = snapshot.HistoryBase.WithPlan(snapshot.HistoryBase.Plan.Rotate([4], 270)) };

        var rebuilt = SessionReplay.Rebuild(mismatch, Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeTrue();
        rebuilt.Plan.ShouldBeSameAs(mismatch.State.Plan);
        rebuilt.History.CanUndo.ShouldBeFalse();
        rebuilt.IsDirty.ShouldBeTrue();
    }

    private static DocumentSession NewSession(params SessionSource[] sources)
        => new(
            Guid.NewGuid(),
            Directory,
            @"C:\docs\a.pdf",
            sources.Length == 0 ? [Original] : sources,
            new SessionState(PagePlan.Identity(SourceIds.Original, 5, RandomPageKeys.Instance), SourceIds.Original, 0));

    private static bool SamePlan(DocumentSession a, DocumentSession b) => a.Plan.SequenceEqual(b.Plan);

    /// <summary>Applies a page operation the way the workspace does: keys recorded, one history step.</summary>
    private void Apply(DocumentSession session, object parameters)
    {
        var operation = _operations.All.OfType<IPlanOperation>().Single(o => o.ParamsType == parameters.GetType());
        var keys = new RecordingPageKeys(RandomPageKeys.Instance);
        var plan = operation.ApplyToPlan(session.Plan, parameters, new PlanEditContext(Sizes, keys));
        var record = new HistoryRecord(operation.Id, parameters, [.. keys.Keys]);
        session.History.Execute(PlanCommand.Create("step", session.State, plan, record));
    }
}
