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
    public void Rebuild_SplitMarksMadeAfterTheSave_ComeBackAndTheDocumentStaysSaved()
    {
        var session = NewSession();
        Apply(session, new RotatePagesParams("1", 90));
        session.MarkSaved();
        Apply(session, new MarkSplitParams("3, 5"));
        Apply(session, new SetSplitMarksParams([new(1, "Tờ trình"), new(3), new(5, "Hợp đồng")]));

        // Through the manifest, as after a crash: parameters are written as JSON and read back.
        var manifest = Infrastructure.Sessions.SessionManifestSerializer.ToManifest(session.CreateSnapshot(), DateTimeOffset.UtcNow);
        var json = Infrastructure.Sessions.SessionManifestSerializer.Serialize(manifest);
        var snapshot = Infrastructure.Sessions.SessionManifestSerializer.ToSnapshot(Infrastructure.Sessions.SessionManifestSerializer.Deserialize(json), _operations.ParameterType);
        var rebuilt = SessionReplay.Rebuild(snapshot, Directory, Sizes, _operations, out var historyLost);

        historyLost.ShouldBeFalse();
        SamePlan(rebuilt, session).ShouldBeTrue();
        rebuilt.Plan[0].SplitMark!.FileName.ShouldBe("Tờ trình");
        rebuilt.IsDirty.ShouldBeFalse("marks are not content");
        rebuilt.History.NextUndoDescription.ShouldBe(Strings.History_SetSplitMarks);
        rebuilt.History.Undo();
        rebuilt.Plan.GetSplitStarts().ShouldBe([0, 2, 4]);
        rebuilt.History.Undo();
        rebuilt.Plan.HasSplitMarks.ShouldBeFalse();
        rebuilt.IsDirty.ShouldBeFalse();
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

    [Fact]
    public void FormSteps_ComeBackFromTheManifest_AsTheyWere()
    {
        var spec = new Operations.Forms.FormFieldSpec(
            "nguoi.hoTen", Core.Forms.FormFieldKind.Text, 0, new PdfRect(100, 700, 300, 720), Core.Forms.FormFieldFlags.Required, "An",
            ToolTip: "Họ và tên", FontSize: 11, Alignment: Core.Forms.FormTextAlignment.Right, MaxLength: 40,
            Format: Core.Forms.FormTextFormat.Number(2), FontFamily: Core.Text.FontFamilies.NotoSerif);
        object[] steps =
        [
            new Operations.Forms.DesignFormParams(
                Remove: ["cu"],
                Add: [spec, spec with { Name = "tinh", Kind = Core.Forms.FormFieldKind.ComboBox, Options = [new("HN", "Hà Nội")], Format = null }],
                Widgets: [new Operations.Forms.FormWidgetChange("a", 1, new PdfRect(1, 2, 30, 40)), new Operations.Forms.FormWidgetChange("b", 0, null)],
                Update: [new Operations.Forms.FormFieldUpdate("c", spec with { Format = Core.Forms.FormTextFormat.Date("yyyy-mm-dd") })]),
            new Operations.Forms.FlattenFormParams(["hoTen"]),
            new Operations.Forms.FillFormParams([new Core.Forms.FormValue("soThich", "doc", ["doc", "nhac"])]),
            new Operations.Forms.ResetFormParams([new Core.Forms.FormValue("hoTen", string.Empty)]),
        ];
        var state = new SessionState(PagePlan.Identity(SourceIds.Original, 5, RandomPageKeys.Instance), SourceIds.Original, 0);
        var snapshot = new SessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Sources = [Original],
            State = state,
            HistoryBase = state,
            Undo = [.. steps.Select(p => new HistoryRecord(_operations.All.Single(o => o.ParamsType == p.GetType()).Id, p, []))],
            Redo = [],
        };
        var created = DateTimeOffset.UtcNow;

        var json = Infrastructure.Sessions.SessionManifestSerializer.Serialize(Infrastructure.Sessions.SessionManifestSerializer.ToManifest(snapshot, created));
        var restored = Infrastructure.Sessions.SessionManifestSerializer.ToSnapshot(Infrastructure.Sessions.SessionManifestSerializer.Deserialize(json), _operations.ParameterType);

        restored.Undo.Select(r => r.Parameters.GetType()).ShouldBe(steps.Select(p => p.GetType()));
        static string WithoutTime(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\"updatedUtc\":\"[^\"]*\"", string.Empty);
        WithoutTime(Infrastructure.Sessions.SessionManifestSerializer.Serialize(Infrastructure.Sessions.SessionManifestSerializer.ToManifest(restored, created)))
            .ShouldBe(WithoutTime(json), "every step and every part of it is written and read back the same");
        restored.Undo[1].Parameters.ShouldBe(steps[1]);
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
        session.History.Execute(PlanCommand.Create("step", session.State, plan, record, operation.ChangesContent));
    }
}
