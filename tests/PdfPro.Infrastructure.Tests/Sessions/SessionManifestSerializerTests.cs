using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Sessions;

namespace PdfPro.Infrastructure.Tests.Sessions;

public sealed class SessionManifestSerializerTests
{
    private const string RotateId = "test.rotate";
    private const string InsertId = "test.insert";

    [Fact]
    public void RoundTrip_RestoresTheSnapshotExactly()
    {
        var session = EditedSession();
        var snapshot = session.CreateSnapshot();
        var stamp = new FileStamp(1234, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        var pending = new PendingSaveDto(@"C:\docs\a.pdf", @"C:\docs\.a.pdf.1.tmp", @"C:\docs\.a.pdf.bak");

        var json = SessionManifestSerializer.Serialize(SessionManifestSerializer.ToManifest(snapshot, DateTimeOffset.UnixEpoch, stamp, pending));
        var manifest = SessionManifestSerializer.Deserialize(json);
        var restored = SessionManifestSerializer.ToSnapshot(manifest, ParameterType);

        manifest.OriginalStamp.ShouldBe(new FileStampDto(1234, stamp.LastWriteTimeUtc));
        manifest.PendingSave.ShouldBe(pending);
        manifest.CreatedUtc.ShouldBe(DateTimeOffset.UnixEpoch);
        restored.SessionId.ShouldBe(snapshot.SessionId);
        restored.OriginalPath.ShouldBe(snapshot.OriginalPath);
        restored.Sources.ShouldBe(snapshot.Sources);
        restored.State.Plan.ToArray().ShouldBe(snapshot.State.Plan.ToArray());
        restored.State.BaseSourceId.ShouldBe("s0");
        restored.HistoryBase.Plan.ToArray().ShouldBe(snapshot.HistoryBase.Plan.ToArray());
        restored.Undo.Select(Describe).ShouldBe(snapshot.Undo.Select(Describe));
        restored.Redo.Select(Describe).ShouldBe(snapshot.Redo.Select(Describe));
        restored.Undo[1].Parameters.ShouldBe(new TestInsert("s1", 2));
        restored.Undo[1].NewPageKeys.ShouldBe(snapshot.Undo[1].NewPageKeys);
        restored.SavePoint.ShouldBe(snapshot.SavePoint);
        restored.IsDirty.ShouldBe(snapshot.IsDirty);
    }

    [Fact]
    public void RoundTrip_KeepsCropsBlankPagesAndRotations()
    {
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance)
            .Crop([0], new PageCrop(1.5, 2.25, 100, 200))
            .InsertBlank(1, new PdfSize(595, 842), RandomPageKeys.Instance)
            .Rotate([1, 2], 270);
        var state = new SessionState(plan, "s0", 0);
        var snapshot = new SessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Sources = [new SessionSource("s0", "sources/s0.pdf", SessionSourceKind.Original, 2)],
            State = state,
            HistoryBase = state,
            Undo = [],
            Redo = [],
            ForcedDirty = true,
        };

        var restored = SessionManifestSerializer.ToSnapshot(
            SessionManifestSerializer.Deserialize(SessionManifestSerializer.Serialize(SessionManifestSerializer.ToManifest(snapshot, DateTimeOffset.UtcNow))),
            ParameterType);

        restored.State.Plan.ToArray().ShouldBe(plan.ToArray());
        restored.ForcedDirty.ShouldBeTrue();
        restored.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void RoundTrip_KeepsSplitMarksAndTheirFileNames()
    {
        var plan = PagePlan.Identity("s0", 4, RandomPageKeys.Instance)
            .WithSplitMarks(new Dictionary<int, SplitMark> { [0] = new("Tờ trình"), [2] = SplitMark.Unnamed });
        var state = new SessionState(plan, "s0", 0);
        var snapshot = new SessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Sources = [new SessionSource("s0", "sources/s0.pdf", SessionSourceKind.Original, 4)],
            State = state,
            HistoryBase = state,
            Undo = [],
            Redo = [],
        };

        var json = SessionManifestSerializer.Serialize(SessionManifestSerializer.ToManifest(snapshot, DateTimeOffset.UtcNow));
        var restored = SessionManifestSerializer.ToSnapshot(SessionManifestSerializer.Deserialize(json), ParameterType);

        restored.State.Plan.ToArray().ShouldBe(plan.ToArray());
        restored.State.Plan[0].SplitMark!.FileName.ShouldBe("Tờ trình");
        restored.State.Plan.GetSplitStarts().ShouldBe([0, 2]);
        json.ShouldNotContain("\"split\":null", Case.Sensitive, "pages without marks write nothing");
    }

    [Fact]
    public void RoundTrip_KeepsAnnotationsWithTheirConversation()
    {
        var created = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
        AnnotationItem[] annotations =
        [
            new MarkupAnnotation("h1", new RgbColor(255, 230, 0), 0.6, "Lan", created, created, "Số liệu sai", MarkupKind.Highlight, [TextQuad.Of(new PdfRect(72, 700, 300, 712))], "Số: 12/2026")
            {
                Replies = [new AnnotationReply("h1-r1", "Minh", created.AddHours(1), "Đã sửa")],
                Reviews = [new AnnotationReview("h1-s1", "Minh", created.AddHours(2), ReviewState.Accepted)],
            },
            new InkAnnotation("i1", RgbColor.Red, 1, "Lan", created, created, "", 2, [[new PdfPoint(10, 10), new PdfPoint(20, 30)]]),
        ];
        var plan = PagePlan.Identity("s0", 3, RandomPageKeys.Instance).WithAnnotations(1, annotations);
        var state = new SessionState(plan, "s0", 0);
        var snapshot = new SessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Sources = [new SessionSource("s0", "sources/s0.pdf", SessionSourceKind.Original, 3)],
            State = state,
            HistoryBase = state,
            Undo = [],
            Redo = [],
        };

        var json = SessionManifestSerializer.Serialize(SessionManifestSerializer.ToManifest(snapshot, DateTimeOffset.UtcNow));
        var restored = SessionManifestSerializer.ToSnapshot(SessionManifestSerializer.Deserialize(json), ParameterType).State.Plan;

        restored[0].Annotations.ShouldBeEmpty();
        restored[1].Annotations.Select(a => a.GetType()).ShouldBe([typeof(MarkupAnnotation), typeof(InkAnnotation)]);
        var highlight = (MarkupAnnotation)restored[1].Annotations[0];
        highlight.Quads.ShouldBe(annotations[0].ShouldBeOfType<MarkupAnnotation>().Quads);
        highlight.Contents.ShouldBe("Số liệu sai");
        highlight.Created.ShouldBe(created);
        highlight.Replies.ShouldHaveSingleItem().Contents.ShouldBe("Đã sửa");
        highlight.State.ShouldBe(ReviewState.Accepted);
        restored.HasSameContent(plan).ShouldBeFalse("annotations are read back as new instances…");
        restored.Select(p => p.PageKey).ShouldBe(plan.Select(p => p.PageKey), "…of the same pages");
        json.ShouldNotContain("\"annotations\":null", Case.Sensitive, "pages without annotations write nothing");
    }

    [Fact]
    public void Deserialize_RejectsInvalidJsonAndNewerSchemas()
    {
        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.Deserialize("{ broken"));
        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.Deserialize("null"));
        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.Deserialize("{\"schemaVersion\": 1}"));

        var json = SessionManifestSerializer.Serialize(SessionManifestSerializer.ToManifest(EditedSession().CreateSnapshot(), DateTimeOffset.UtcNow));
        var newer = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);
        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.Deserialize(newer));
    }

    [Fact]
    public void ToSnapshot_RejectsUnknownOperations()
    {
        var manifest = SessionManifestSerializer.ToManifest(EditedSession().CreateSnapshot(), DateTimeOffset.UtcNow);

        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.ToSnapshot(manifest, _ => null));
    }

    [Fact]
    public void ToSnapshot_RejectsInvalidPlans()
    {
        var manifest = SessionManifestSerializer.ToManifest(EditedSession().CreateSnapshot(), DateTimeOffset.UtcNow);
        var duplicated = manifest with { State = manifest.State with { Plan = [manifest.State.Plan[0], manifest.State.Plan[0]] } };

        Should.Throw<SessionManifestException>(() => SessionManifestSerializer.ToSnapshot(duplicated, ParameterType));
    }

    [Fact]
    public void IsDirty_ComparesTheSavePointWithTheCurrentPosition()
    {
        var manifest = SessionManifestSerializer.ToManifest(EditedSession().CreateSnapshot(), DateTimeOffset.UtcNow);

        (manifest with { SavePoint = manifest.Undo.Count }).IsDirty.ShouldBeFalse();
        (manifest with { SavePoint = 0 }).IsDirty.ShouldBeTrue();
        (manifest with { SavePoint = null }).IsDirty.ShouldBeTrue();
        (manifest with { SavePoint = manifest.Undo.Count, ForcedDirty = true }).IsDirty.ShouldBeTrue();
    }

    /// <summary>s0 (5 pages) + s1 (3 pages); rotate, insert from s1, rotate again, then undo once (one redo step).</summary>
    private static DocumentSession EditedSession()
    {
        var sources = new[]
        {
            new SessionSource("s0", "sources/s0.pdf", SessionSourceKind.Original, 5),
            new SessionSource("s1", "sources/s1.pdf", SessionSourceKind.Inserted, 3, "b.pdf"),
        };
        var session = new DocumentSession(Guid.NewGuid(), @"C:\sessions\x", @"C:\docs\a.pdf", sources, new SessionState(PagePlan.Identity("s0", 5, RandomPageKeys.Instance), "s0", 0));

        Execute(session, RotateId, new TestRotate("1", 90), p => p.Rotate([0], 90), []);
        var keys = new RecordingPageKeys(RandomPageKeys.Instance);
        var inserted = session.Plan.InsertFromSource(1, "s1", [2], keys);
        Execute(session, InsertId, new TestInsert("s1", 2), _ => inserted, keys.Keys);
        session.MarkSaved();
        Execute(session, RotateId, new TestRotate("2", 180), p => p.Rotate([1], 180), []);
        session.History.Undo();
        return session;
    }

    private static void Execute(DocumentSession session, string operationId, object parameters, Func<PagePlan, PagePlan> edit, IReadOnlyList<Guid> keys)
        => session.History.Execute(PlanCommand.Create(operationId, session.State, edit(session.Plan), new HistoryRecord(operationId, parameters, [.. keys])));

    private static Type? ParameterType(string operationId) => operationId switch
    {
        RotateId => typeof(TestRotate),
        InsertId => typeof(TestInsert),
        _ => null,
    };

    private static string Describe(HistoryRecord record)
        => $"{record.OperationId}|{record.Parameters}|{string.Join(",", record.NewPageKeys)}|{record.NewSourceId}";

    private sealed record TestRotate(string Pages, int Degrees);

    private sealed record TestInsert(string SourceId, int Page);
}
