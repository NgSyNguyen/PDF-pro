using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Core.Tests.Organizing;

namespace PdfPro.Core.Tests.Sessions;

public sealed class DocumentSessionTests
{
    private static readonly SessionSource Original = new("s0", "sources/s0.pdf", SessionSourceKind.Original, 5);

    private readonly DocumentSession _session = CreateSession();

    [Fact]
    public void NewSession_StartsCleanOnTheOriginal()
    {
        _session.Plan.Count.ShouldBe(5);
        _session.BaseSourceId.ShouldBe("s0");
        _session.CurrentRevision.ShouldBe(0);
        _session.IsDirty.ShouldBeFalse();
        _session.History.CanUndo.ShouldBeFalse();
        _session.Sources.Keys.ShouldBe(["s0"]);
        _session.OriginalPath.ShouldBe(@"C:\docs\a.pdf");
    }

    [Fact]
    public void Constructor_ValidatesItsArguments()
    {
        var state = new SessionState(Plans.Identity(5), "s0", 0);

        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.Empty, "dir", null, [Original], state));
        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), " ", null, [Original], state));
        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), "dir", "", [Original], state));
        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), "dir", null, [Original, Original], state));
        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), "dir", null, [], state));
    }

    [Fact]
    public void Constructor_RejectsPlansThatDoNotFitTheSources()
    {
        var tooLong = new SessionState(Plans.Identity(6), "s0", 0);
        var unknown = new SessionState(Plans.Identity(2, "s1"), "s0", 0);

        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), "dir", null, [Original], tooLong));
        Should.Throw<ArgumentException>(() => new DocumentSession(Guid.NewGuid(), "dir", null, [Original], unknown));
    }

    [Fact]
    public void Edit_ChangesTheStateAndMakesTheDocumentDirty()
    {
        var states = new List<SessionStateChangedEventArgs>();
        var dirtyChanges = 0;
        _session.StateChanged += (_, e) => states.Add(e);
        _session.DirtyChanged += (_, _) => dirtyChanges++;
        var before = _session.State;

        _session.History.Execute(Rotate(_session, [1]));

        _session.Plan[1].RotationDelta.ShouldBe(90);
        _session.IsDirty.ShouldBeTrue();
        dirtyChanges.ShouldBe(1);
        states.Count.ShouldBe(1);
        states[0].OldState.ShouldBeSameAs(before);
        states[0].NewState.ShouldBeSameAs(_session.State);
    }

    [Fact]
    public void Undo_RestoresTheExactPreviousState()
    {
        var before = _session.State;
        _session.History.Execute(Rotate(_session, [1]));

        _session.History.Undo();

        _session.State.ShouldBeSameAs(before);
        _session.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void ThirtyEditsUndoneOneByOne_ReturnToTheStart()
    {
        var start = _session.State;
        for (var i = 0; i < 30; i++)
        {
            _session.History.Execute(i % 3 == 0 ? Rotate(_session, [i % 5]) : Move(_session, [i % 5], 0));
        }

        while (_session.History.CanUndo)
        {
            _session.History.Undo();
        }

        _session.State.ShouldBeSameAs(start);
        _session.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void MarkSaved_MovesTheSavePoint()
    {
        _session.History.Execute(Rotate(_session, [0]));

        _session.MarkSaved();
        _session.IsDirty.ShouldBeFalse();

        _session.History.Undo();
        _session.IsDirty.ShouldBeTrue();
        _session.History.Redo();
        _session.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void MarkUnsaved_ForcesDirtyUntilTheNextSave()
    {
        var dirtyChanges = 0;
        _session.DirtyChanged += (_, _) => dirtyChanges++;

        _session.MarkUnsaved();
        _session.IsDirty.ShouldBeTrue();

        _session.MarkSaved();
        _session.IsDirty.ShouldBeFalse();
        dirtyChanges.ShouldBe(2);
    }

    [Fact]
    public void ExecutingACommandTwice_FailsAndLeavesTheHistoryUnchanged()
    {
        var command = Rotate(_session, [0]);
        _session.History.Execute(command);

        Should.Throw<InvalidOperationException>(() => _session.History.Execute(command));

        _session.History.UndoCount.ShouldBe(1);
    }

    [Fact]
    public void AddSource_MakesItsPagesUsable()
    {
        var raised = 0;
        _session.SourcesChanged += (_, _) => raised++;
        var inserted = new SessionSource("s1", "sources/s1.pdf", SessionSourceKind.Inserted, 3, "b.pdf");
        var plan = _session.Plan.InsertFromSource(0, "s1", [2], new SequentialKeys(100));
        var command = PlanCommand.Create("Insert", _session.State, plan, Record("organize.insertPages"));

        Should.Throw<ArgumentException>(() => _session.History.Execute(command));
        _session.AddSource(inserted);
        _session.History.Execute(command);

        _session.Plan[0].SourceId.ShouldBe("s1");
        raised.ShouldBe(1);
        Should.Throw<ArgumentException>(() => _session.AddSource(inserted));
    }

    [Fact]
    public void NextSourceId_NumbersInsertedFilesAndRevisions()
    {
        _session.NextSourceId(SessionSourceKind.Inserted).ShouldBe("s1");
        _session.NextSourceId(SessionSourceKind.Revision).ShouldBe("r1");
        _session.AddSource(new SessionSource("s1", "sources/s1.pdf", SessionSourceKind.Inserted, 1));
        _session.NextSourceId(SessionSourceKind.Inserted).ShouldBe("s2");
        Should.Throw<ArgumentOutOfRangeException>(() => _session.NextSourceId(SessionSourceKind.Original));
    }

    [Fact]
    public void RevisionCommand_SwitchesToTheRevisionAndBack()
    {
        _session.History.Execute(Rotate(_session, [0]));
        var before = _session.State;
        _session.AddSource(new SessionSource("r1", "revisions/r0001.pdf", SessionSourceKind.Revision, 5));
        var keys = new RecordingPageKeys(new SequentialKeys(100));
        var after = new SessionState(PagePlan.Identity("r1", 5, keys), "r1", 1);

        _session.History.Execute(new RevisionCommand("Page size", before, after, Record("organize.pageSize", keys.Keys, "r1")));

        _session.BaseSourceId.ShouldBe("r1");
        _session.CurrentRevision.ShouldBe(1);
        _session.Plan.ShouldAllBe(p => p.SourceId == "r1");
        _session.History.Undo();
        _session.State.ShouldBeSameAs(before);
        _session.CurrentRevision.ShouldBe(0);
    }

    [Fact]
    public void PlanCommand_CannotChangeTheRevision()
    {
        var before = _session.State;
        var other = new SessionState(before.Plan, "s0", 1);

        Should.Throw<ArgumentException>(() => new PlanCommand("x", before, other, Record("organize.rotate")));
        Should.Throw<ArgumentException>(() => new PlanCommand("x", before, before, Record("organize.rotate", [], "r1")));
    }

    [Fact]
    public void RevisionCommand_NeedsANewerRevisionNamedByTheRecord()
    {
        var before = _session.State;
        var after = new SessionState(before.Plan, "s0", 1);

        Should.Throw<ArgumentException>(() => new RevisionCommand("x", before, before, Record("organize.pageSize", [], "s0")));
        Should.Throw<ArgumentException>(() => new RevisionCommand("x", before, after, Record("organize.pageSize", [], "r1")));
    }

    [Fact]
    public void Commands_NeedADescription()
        => Should.Throw<ArgumentException>(() => PlanCommand.Create(" ", _session.State, _session.Plan, Record("organize.rotate")));

    [Fact]
    public void CreateSnapshot_CapturesStateHistoryAndSavePoint()
    {
        var start = _session.State;
        var first = Rotate(_session, [0]);
        _session.History.Execute(first);
        _session.MarkSaved();
        var second = Move(_session, [4], 0);
        _session.History.Execute(second);
        var third = Rotate(_session, [2]);
        _session.History.Execute(third);
        _session.History.Undo();

        var snapshot = _session.CreateSnapshot();

        snapshot.SessionId.ShouldBe(_session.Id);
        snapshot.OriginalPath.ShouldBe(@"C:\docs\a.pdf");
        snapshot.Sources.ShouldBe([Original]);
        snapshot.State.ShouldBeSameAs(_session.State);
        snapshot.HistoryBase.ShouldBeSameAs(start);
        snapshot.Undo.ShouldBe([first.Record, second.Record]);
        snapshot.Redo.ShouldBe([third.Record]);
        snapshot.SavePoint.ShouldBe(1);
        snapshot.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void CreateSnapshot_WithoutHistory_StartsFromTheCurrentState()
    {
        _session.MarkUnsaved();

        var snapshot = _session.CreateSnapshot();

        snapshot.HistoryBase.ShouldBeSameAs(_session.State);
        snapshot.Undo.ShouldBeEmpty();
        snapshot.ForcedDirty.ShouldBeTrue();
        snapshot.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void SetOriginalPath_ChangesWhereTheDocumentIsSaved()
    {
        _session.SetOriginalPath(@"D:\b.pdf");

        _session.OriginalPath.ShouldBe(@"D:\b.pdf");
        Should.Throw<ArgumentException>(() => _session.SetOriginalPath(""));
    }

    [Theory]
    [InlineData("s0", "../s0.pdf")]
    [InlineData("s0", @"C:\s0.pdf")]
    [InlineData("s0", "sources//s0.pdf")]
    [InlineData("blank", "sources/blank.pdf")]
    [InlineData("S0", "sources/s0.pdf")]
    public void SessionSource_RejectsUnsafeIdsAndPaths(string id, string fileName)
        => Should.Throw<ArgumentException>(() => new SessionSource(id, fileName, SessionSourceKind.Original, 1));

    [Fact]
    public void SessionSource_NeedsPagesAndAKnownKind()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SessionSource("s0", "s0.pdf", SessionSourceKind.Original, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new SessionSource("s0", "s0.pdf", (SessionSourceKind)9, 1));
    }

    [Fact]
    public void SessionState_ValidatesItsParts()
    {
        Should.Throw<ArgumentException>(() => new SessionState(PagePlan.Create([]), "s0", 0));
        Should.Throw<ArgumentException>(() => new SessionState(Plans.Identity(1), SourceIds.Blank, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new SessionState(Plans.Identity(1), "s0", -1));
        var state = new SessionState(Plans.Identity(1), "s0", 0);
        state.WithPlan(state.Plan).ShouldBeSameAs(state);
    }

    [Fact]
    public void HistoryRecord_ValidatesItsParts()
    {
        Should.Throw<ArgumentException>(() => new HistoryRecord("rotate", new object(), []));
        Should.Throw<ArgumentException>(() => new HistoryRecord("organize.rotate", new object(), [Guid.Empty]));
        Should.Throw<ArgumentException>(() => new HistoryRecord("organize.rotate", new object(), [], "R1"));
        Should.Throw<ArgumentNullException>(() => new HistoryRecord("organize.rotate", null!, []));
    }

    private static DocumentSession CreateSession()
        => new(Guid.NewGuid(), @"C:\sessions\x", @"C:\docs\a.pdf", [Original], new SessionState(Plans.Identity(5), "s0", 0));

    private static PlanCommand Rotate(DocumentSession session, int[] pages)
        => PlanCommand.Create("Rotate", session.State, session.Plan.Rotate(pages, 90), Record("organize.rotate"));

    private static PlanCommand Move(DocumentSession session, int[] pages, int target)
        => PlanCommand.Create("Move", session.State, session.Plan.Move(pages, target), Record("organize.move"));

    private static HistoryRecord Record(string operationId, IReadOnlyList<Guid>? keys = null, string? newSourceId = null)
        => new(operationId, new object(), keys ?? [], newSourceId);
}
